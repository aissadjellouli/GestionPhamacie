using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class SurveillanceEnMemoire(params AnomalieNouvelle[] nouvelles) : ISurveillance
{
    public List<EvenementCaisse> Evenements { get; } = [];
    /// <summary>Journées calculées ; null : la date du serveur PostgreSQL.</summary>
    public List<DateOnly?> Calculs { get; } = [];
    public ParametresSurveillance Parametres { get; set; } = new(5, 3, 3, 5, 3, null, null);
    public List<(long Id, string? Action)> Traitees { get; } = [];

    public Task EnregistrerAsync(EvenementCaisse e, CancellationToken ct = default) { Evenements.Add(e); return Task.CompletedTask; }
    public Task<IReadOnlyList<AnomalieNouvelle>> SurveillerAsync(DateOnly? jour, CancellationToken ct = default)
    {
        Calculs.Add(jour);
        return Task.FromResult<IReadOnlyList<AnomalieNouvelle>>(nouvelles);
    }
    public Task<IReadOnlyList<AlerteSurveillance>> ListerAsync(bool traitees, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AlerteSurveillance>>([]);
    public Task<bool> TraiterAsync(long id, string? action, long u, CancellationToken ct = default) { Traitees.Add((id, action)); return Task.FromResult(true); }
    public Task<ParametresSurveillance> ParametresAsync(CancellationToken ct = default) => Task.FromResult(Parametres);
    public Task EnregistrerParametresAsync(ParametresSurveillance p, CancellationToken ct = default) { Parametres = p; return Task.CompletedTask; }
    public Dictionary<long, string> Produits { get; } = new() { [18] = "CREME MAINS" };
    public HashSet<long> Sensibles { get; } = [];
    public Task<IReadOnlyList<ProduitSensible>> SensiblesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitSensible>>(Sensibles.Select(id => new ProduitSensible(id, Produits[id], 9)).ToList());
    public Task<string?> MarquerSensibleAsync(long produitId, bool sensible, CancellationToken ct = default)
    {
        if (!Produits.TryGetValue(produitId, out var nom)) return Task.FromResult<string?>(null);
        if (sensible) Sensibles.Add(produitId); else Sensibles.Remove(produitId);
        return Task.FromResult<string?>(nom);
    }
}

public class SurveillanceTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 11));

    private static (SignalerEvenementCaisse Cas, SurveillanceEnMemoire Depot, AlertesTitulaireEspion Titulaire) Signaler(DroitsFactices droits,
        params AnomalieNouvelle[] nouvelles)
    {
        var (depot, titulaire) = (new SurveillanceEnMemoire(nouvelles), new AlertesTitulaireEspion());
        var cas = new SignalerEvenementCaisse(depot, new SessionsEnMemoire(), droits, new Surveiller(depot, titulaire), new JournalEspion());
        return (cas, depot, titulaire);
    }

    [Fact]
    public async Task Ligne_retiree_enregistree_avec_la_caisse_du_poste_puis_anomalies_notifiees()
    {
        var (cas, depot, titulaire) = Signaler(new DroitsFactices("caisse.vendre"), new AnomalieNouvelle(4, "urgent", "Vendeur Démo : 10 boite(s) retiree(s)"));
        await cas.ExecuterAsync("ligne_retiree", 2, 20, 3, 1050, 5, "Caisse 1");

        var e = depot.Evenements.Single();
        Assert.Equal(("ligne_retiree", 5L, "Caisse 1", 1L, 3, 1050m), (e.Type, e.UtilisateurId, e.Poste, e.SessionCaisseId!.Value, e.Quantite!.Value, e.Montant!.Value));
        Assert.Equal([(DateOnly?)null], depot.Calculs);            // journée du serveur PostgreSQL
        Assert.Equal([("Vendeur Démo : 10 boite(s) retiree(s)", true)], titulaire.Surveillances);
    }

    [Fact]
    public async Task Tiroir_sans_vente_demande_le_droit()
    {
        var (sans, depot, _) = Signaler(new DroitsFactices("caisse.vendre"));
        await Assert.ThrowsAsync<DroitRefuseException>(() => sans.ExecuterAsync("tiroir_ouvert", null, null, null, null, 5, "Caisse 1"));
        Assert.Empty(depot.Evenements);

        var (avec, depot2, _) = Signaler(new DroitsFactices("caisse.vendre", "caisse.ouvrir_tiroir"));
        await avec.ExecuterAsync("tiroir_ouvert", 2, 20, 1, 100, 5, "Caisse 1");
        Assert.Equal(("tiroir_ouvert", (long?)null), (depot2.Evenements.Single().Type, depot2.Evenements.Single().ProduitId));
    }

    [Theory]
    [InlineData("vente_effacee", 2, 1)]
    [InlineData("attente_abandonnee", 2, 1)]   // inscrit par le serveur, jamais par le poste
    [InlineData("ligne_retiree", null, 1)]
    [InlineData("ligne_retiree", 2, 0)]
    public async Task Evenement_invalide_refuse(string type, int? produit, int quantite)
    {
        var (cas, depot, _) = Signaler(new DroitsFactices("caisse.vendre"));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(type, produit, null, quantite, null, 5, "Caisse 1"));
        Assert.Empty(depot.Evenements);
    }

    [Fact]
    public async Task Vente_en_attente_abandonnee_inscrite_avec_l_employe_et_le_montant()
    {
        var (attente, depot) = (new AttenteEnMemoire(), new SurveillanceEnMemoire());
        var gerer = new GererVentesEnAttente(attente, depot, new Surveiller(depot));
        var id = await gerer.MettreEnAttenteAsync("Caisse 1", 5, "Mme A", 2, 1250, "{}");
        await gerer.SupprimerAsync(id, "Caisse 1", utilisateurId: 5);

        var e = depot.Evenements.Single();
        Assert.Equal(("attente_abandonnee", 5L, 1250m), (e.Type, e.UtilisateurId, e.Montant!.Value));
        Assert.Single(depot.Calculs);
    }

    [Fact]
    public async Task Surveillance_reservee_au_titulaire_et_reglages_controles()
    {
        var depot = new SurveillanceEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ListerSurveillance(depot, new DroitsFactices("admin.parametres")).ExecuterAsync(false, 5));
        await Assert.ThrowsAsync<DroitRefuseException>(() => new TraiterSurveillance(depot, new DroitsFactices()).ExecuterAsync(1, null, 5));

        var regler = new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(depot.Parametres with { HeureOuverture = new TimeOnly(8, 0) }, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            regler.EnregistrerAsync(depot.Parametres with { HeureOuverture = new TimeOnly(8, 0), HeureFermeture = new TimeOnly(8, 0) }, 1));
        // Plage qui passe minuit acceptée
        await regler.EnregistrerAsync(depot.Parametres with { HeureOuverture = new TimeOnly(8, 0), HeureFermeture = new TimeOnly(0, 30) }, 1);
        Assert.Equal(new TimeOnly(0, 30), depot.Parametres.HeureFermeture);
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(depot.Parametres with { SeuilTiroir = 0 }, 1));

        await regler.EnregistrerAsync(depot.Parametres with { HeureOuverture = new TimeOnly(8, 30), HeureFermeture = new TimeOnly(20, 0) }, 1);
        Assert.Equal(new TimeOnly(20, 0), depot.Parametres.HeureFermeture);
    }

    [Fact]
    public async Task Echec_du_calcul_inscrit_au_journal_du_serveur()
    {
        var trace = new TraceEspion();
        await new Surveiller(new SurveillanceEnPanne(), null, trace).ExecuterAsync();     // ne lève pas
        Assert.Single(trace.Erreurs);
    }

    private sealed class TraceEspion : ITraceErreurs
    {
        public List<Exception> Erreurs { get; } = [];
        public void Erreur(string message, Exception exception) => Erreurs.Add(exception);
    }

    private sealed class SurveillanceEnPanne : ISurveillance
    {
        public Task EnregistrerAsync(EvenementCaisse e, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AnomalieNouvelle>> SurveillerAsync(DateOnly? jour, CancellationToken ct = default) => throw new InvalidOperationException("base");
        public Task<IReadOnlyList<AlerteSurveillance>> ListerAsync(bool t, int l, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> TraiterAsync(long id, string? a, long u, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ParametresSurveillance> ParametresAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task EnregistrerParametresAsync(ParametresSurveillance p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProduitSensible>> SensiblesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<string?> MarquerSensibleAsync(long p, bool s, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Echec_de_notification_ne_bloque_pas()
    {
        var depot = new SurveillanceEnMemoire(new AnomalieNouvelle(1, "attention", "x"));
        await new Surveiller(depot, new AlertesEnPanne()).ExecuterAsync();
        Assert.Single(depot.Calculs);
    }

    private sealed class AlertesEnPanne : Notifications.IAlertesTitulaire
    {
        public Task EcartCaisseAsync(string e, string p, decimal ecart, decimal seuil, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string lot, int stock, int vendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool d, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => throw new InvalidOperationException("SMTP injoignable");
    }
}
