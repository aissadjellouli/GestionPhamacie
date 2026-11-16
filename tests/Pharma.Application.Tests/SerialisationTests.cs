using Pharma.Application.Notifications;
using Pharma.Application.Serialisation;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Sérialisation DataMatrix (script 120, lot 10) : file hors ligne rejouée, statuts, vente jamais bloquée.</summary>
public class SerialisationTests
{
    private static readonly BoiteSerialisee Boite = new("03400000000017", "SER1", "L1", new DateOnly(2030, 1, 31));
    private static readonly DateOnly Aujourdhui = new(2026, 11, 16);

    [Fact]
    public async Task Hors_ligne_la_file_reste_en_attente_sans_erreur_puis_est_rejouee()
    {
        var file = new FileMemoire();
        file.Ajouter(OperationSerialisation.Decommissionner);
        file.Ajouter(OperationSerialisation.Verifier);
        var connecteur = new ConnecteurFactice { HorsLigne = true };
        var traiter = new TraiterFileSerialisation(file, connecteur);

        var b1 = await traiter.ExecuterAsync();   // ne lève pas : la vente déjà faite n'est pas remise en cause
        Assert.True(b1.ConnecteurIndisponible);
        Assert.All(file.Lignes, l => Assert.Equal("en_attente", l.Statut));
        Assert.Equal(1, file.Lignes[0].Essais);

        connecteur.HorsLigne = false;
        var b2 = await traiter.ExecuterAsync();
        Assert.False(b2.ConnecteurIndisponible);
        Assert.Equal(2, b2.Reussies);
        Assert.All(file.Lignes, l => Assert.Equal("reussie", l.Statut));
        Assert.Equal(["decommissionner", "verifier"], connecteur.Appels);
    }

    [Theory]
    [InlineData(StatutSerialisation.Inconnue)]
    [InlineData(StatutSerialisation.DejaDecommissionnee)]
    [InlineData(StatutSerialisation.Rappelee)]
    [InlineData(StatutSerialisation.Expiree)]
    public async Task Boite_refusee_apres_coup_est_tracee_et_signalee_au_titulaire(StatutSerialisation statut)
    {
        var file = new FileMemoire();
        file.Ajouter(OperationSerialisation.Decommissionner);
        var alertes = new AlertesFactices();
        var bilan = await new TraiterFileSerialisation(file, new ConnecteurFactice { Reponse = statut }, alertes).ExecuterAsync();

        Assert.Equal(1, bilan.Refusees);
        Assert.Equal("refusee", file.Lignes[0].Statut);
        Assert.Equal(statut, file.Lignes[0].Resultat);
        Assert.True(file.Lignes[0].Alerte);
        var (message, urgent) = Assert.Single(alertes.Messages);
        Assert.True(urgent);
        Assert.Contains("SER1", message);
        Assert.Contains("vente est déjà faite", message);
    }

    [Fact]
    public async Task Authentique_reussie_sans_alerte()
    {
        var file = new FileMemoire();
        file.Ajouter(OperationSerialisation.Reactiver);
        var alertes = new AlertesFactices();
        await new TraiterFileSerialisation(file, new ConnecteurFactice(), alertes).ExecuterAsync();
        Assert.Equal("reussie", file.Lignes[0].Statut);
        Assert.Empty(alertes.Messages);
    }

    [Fact]
    public async Task Alerte_en_echec_ne_bloque_pas_le_traitement()
    {
        var file = new FileMemoire();
        file.Ajouter(OperationSerialisation.Decommissionner);
        file.Ajouter(OperationSerialisation.Decommissionner);
        var bilan = await new TraiterFileSerialisation(file, new ConnecteurFactice { Reponse = StatutSerialisation.Rappelee },
            new AlertesFactices { Echoue = true }).ExecuterAsync();
        Assert.Equal(2, bilan.Refusees);
    }

    [Fact]
    public async Task Relance_d_une_operation_deja_reussie_est_refusee()
    {
        var file = new FileMemoire();
        var piloter = new PiloterFileSerialisation(file, new TraiterFileSerialisation(file, new ConnecteurFactice()));
        await Assert.ThrowsAsync<RegleMetierException>(() => piloter.RelancerAsync(42, 1));
    }

    [Fact]
    public async Task Statut_de_filtre_inconnu_refuse()
    {
        var file = new FileMemoire();
        var piloter = new PiloterFileSerialisation(file, new TraiterFileSerialisation(file, new ConnecteurFactice()));
        await Assert.ThrowsAsync<RegleMetierException>(() => piloter.ListerAsync("perdue", 1, 10));
    }

    // ---- Adaptateur factice : réponses déterministes ----

    [Fact]
    public void Factice_sans_regle_repond_authentique()
    {
        Assert.Equal(StatutSerialisation.Authentique, ReponsesFactices.Repondre([], OperationSerialisation.Verifier, Boite, Aujourdhui).Statut);
    }

    [Fact]
    public void Factice_premiere_regle_correspondante_l_emporte()
    {
        RegleReponseFactice[] regles =
        [
            new(null, "AUTRE", null, null, StatutSerialisation.Inconnue),
            new("03400000000017", "SER1", null, OperationSerialisation.Decommissionner, StatutSerialisation.DejaDecommissionnee),
            new(null, null, "L1", null, StatutSerialisation.Rappelee)
        ];
        Assert.Equal(StatutSerialisation.DejaDecommissionnee,
            ReponsesFactices.Repondre(regles, OperationSerialisation.Decommissionner, Boite, Aujourdhui).Statut);
        Assert.Equal(StatutSerialisation.Rappelee, ReponsesFactices.Repondre(regles, OperationSerialisation.Verifier, Boite, Aujourdhui).Statut);
    }

    [Fact]
    public void Factice_boite_perimee_expiree_sauf_reactivation()
    {
        var perimee = Boite with { Peremption = new DateOnly(2026, 10, 31) };
        Assert.Equal(StatutSerialisation.Expiree, ReponsesFactices.Repondre([], OperationSerialisation.Decommissionner, perimee, Aujourdhui).Statut);
        Assert.Equal(StatutSerialisation.Authentique, ReponsesFactices.Repondre([], OperationSerialisation.Reactiver, perimee, Aujourdhui).Statut);
    }

    [Fact]
    public void Codes_des_statuts_aller_retour()
    {
        foreach (var s in Enum.GetValues<StatutSerialisation>())
            Assert.Equal(s, LibellesSerialisation.DepuisCode(LibellesSerialisation.Code(s)));
        foreach (var o in Enum.GetValues<OperationSerialisation>())
            Assert.Equal(o, LibellesSerialisation.OperationDepuisCode(LibellesSerialisation.Code(o)));
    }

    // ---- Doublures ----

    private sealed class Ligne(long id, OperationSerialisation operation)
    {
        public long Id { get; } = id;
        public OperationSerialisation Operation { get; } = operation;
        public string Statut { get; set; } = "en_attente";
        public StatutSerialisation? Resultat { get; set; }
        public int Essais { get; set; }
        public bool Alerte { get; set; }
    }

    private sealed class FileMemoire : IFileSerialisation
    {
        public List<Ligne> Lignes { get; } = [];
        public void Ajouter(OperationSerialisation o) => Lignes.Add(new Ligne(Lignes.Count + 1, o));

        public Task<IReadOnlyList<OperationEnFile>> AFaireAsync(int maximum, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<OperationEnFile>>(Lignes.Where(l => l.Statut == "en_attente").Take(maximum)
                .Select(l => new OperationEnFile(l.Id, l.Id, l.Operation, Boite, "Doliprane", l.Essais)).ToList());

        public Task TerminerAsync(long id, bool acceptee, StatutSerialisation resultat, string? message, bool alerteTitulaire, CancellationToken ct = default)
        {
            var l = Lignes.Single(x => x.Id == id);
            (l.Statut, l.Resultat, l.Alerte) = (acceptee ? "reussie" : "refusee", resultat, alerteTitulaire);
            l.Essais++;
            return Task.CompletedTask;
        }

        public Task ReporterAsync(long id, string message, CancellationToken ct = default)
        {
            Lignes.Single(x => x.Id == id).Essais++;
            return Task.CompletedTask;
        }

        public Task<int> RelancerAsync(long? id, long utilisateurId, CancellationToken ct = default) =>
            Task.FromResult(Lignes.Count(l => (id == null || l.Id == id) && l.Statut != "reussie"));

        public Task<EtatFileSerialisation> EtatAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(IReadOnlyList<LigneFileSerialisation> Lignes, int Total)> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<LigneFileSerialisation>, int)>(([], 0));
        public Task<ParametresSerialisation> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresSerialisation(true, "factice", false));
        public Task EnregistrerParametresAsync(bool actif, bool facticeHorsLigne, long utilisateurId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Gs1Identifiant>> IdentifiantsGs1Async(CancellationToken ct = default) =>
            Task.FromResult(Gs1Norme.ParDefaut.Identifiants);
    }

    private sealed class ConnecteurFactice : ISerialisationConnecteur
    {
        public bool HorsLigne { get; set; }
        public StatutSerialisation Reponse { get; set; } = StatutSerialisation.Authentique;
        public List<string> Appels { get; } = [];
        public string Nom => "Factice de test";

        private Task<ReponseSerialisation> Repondre(string op)
        {
            if (HorsLigne) throw new ConnecteurIndisponibleException("hors ligne");
            Appels.Add(op);
            return Task.FromResult(new ReponseSerialisation(Reponse));
        }

        public Task<ReponseSerialisation> VerifierAsync(BoiteSerialisee boite, CancellationToken ct = default) => Repondre("verifier");
        public Task<ReponseSerialisation> DecommissionnerAsync(BoiteSerialisee boite, CancellationToken ct = default) => Repondre("decommissionner");
        public Task<ReponseSerialisation> ReactiverAsync(BoiteSerialisee boite, CancellationToken ct = default) => Repondre("reactiver");
    }

    private sealed class AlertesFactices : IAlertesTitulaire
    {
        public bool Echoue { get; init; }
        public List<(string Message, bool Urgent)> Messages { get; } = [];
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default)
        {
            if (Echoue) throw new InvalidOperationException("file des notifications indisponible");
            Messages.Add((message, urgent));
            return Task.CompletedTask;
        }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
