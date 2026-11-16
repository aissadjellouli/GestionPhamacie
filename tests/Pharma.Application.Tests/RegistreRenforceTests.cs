using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Psychotropes;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Registre des psychotropes renforcé (lot 8) : alertes, forçage, garde de l'encaissement, annulation, exports, concordance.</summary>
public class RegistreRenforceTests
{
    private static readonly IReadOnlyList<RegleAlertePsychotrope> Regles =
    [
        new(CodesAlertePsychotrope.PatientRepete, "Patient", true, 2, 30, false),
        new(CodesAlertePsychotrope.OrdonnanceReutilisee, "Ordonnance", true, 1, 365, false),
        new(CodesAlertePsychotrope.QuantiteAnormale, "Quantité", true, 3, 28, false),
        new(CodesAlertePsychotrope.PrescripteurInhabituel, "Prescripteur", true, 1, 365, true)
    ];

    private static readonly Dictionary<long, string> Produits = new() { [7] = "Alprazolam" };

    private static DelivrancePsychotrope Delivrance(int boites = 1, string? numero = "A12", int? duree = null) =>
        new(1, 3, numero, new DateOnly(2026, 11, 10), [new LignePsychotrope(7, boites)], duree);

    [Fact]
    public void Aucune_alerte_sous_les_seuils()
    {
        var alertes = EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(), new HistoriquePsychotrope(1, 0, 5), Produits);
        Assert.Empty(alertes);
    }

    [Fact]
    public void Chaque_regle_se_declenche_a_son_seuil_sans_nom_de_patient()
    {
        var alertes = EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(boites: 4, duree: 60), new HistoriquePsychotrope(2, 1, 0), Produits);
        Assert.Equal(Regles.Select(r => r.Code).OrderBy(c => c), alertes.Select(a => a.Code).OrderBy(c => c));
        var quantite = alertes.Single(a => a.Code == CodesAlertePsychotrope.QuantiteAnormale).Message;
        Assert.Contains("Alprazolam : 4 boîtes", quantite);
        Assert.Contains("60 jours", quantite);
        Assert.True(alertes.Single(a => a.Code == CodesAlertePsychotrope.PrescripteurInhabituel).ExigeValideur);
    }

    [Fact]
    public void Regle_inactive_ou_donnee_inconnue_ne_declenche_rien()
    {
        var inactives = Regles.Select(r => r with { Active = false }).ToList();
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(inactives, Delivrance(boites: 9), new HistoriquePsychotrope(9, 9, 0), Produits));
        // Sans n° d'ordonnance : pas de contrôle de réutilisation ; sans durée : seulement les boîtes
        var sansNumero = EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(numero: null), new HistoriquePsychotrope(0, 5, 3), Produits);
        Assert.Empty(sansNumero);
    }

    [Fact]
    public async Task Forcer_exige_un_motif_puis_garde_l_employe()
    {
        var depot = new DepotFactice { Historique = new(3, 0, 5) };
        var forcer = new ForcerAlertesPsychotropes(depot, new DroitsFactices(Permissions.PsychotropesDelivrer));
        await Assert.ThrowsAsync<RegleMetierException>(() => forcer.ExecuterAsync(4, Delivrance(), "ok", null));
        var id = await forcer.ExecuterAsync(4, Delivrance(), "Traitement de fond connu", "CAISSE-1");
        Assert.NotNull(id);
        var f = Assert.Single(depot.Forcages);
        Assert.Equal(4, f.UtilisateurId);
        Assert.Null(f.ValidePar);
        Assert.Equal([CodesAlertePsychotrope.PatientRepete], f.Codes);
        Assert.Equal("Traitement de fond connu", f.Motif);
    }

    [Fact]
    public async Task Regle_avec_valideur_demande_le_droit_puis_garde_le_valideur()
    {
        var depot = new DepotFactice { Historique = new(0, 0, 0) };   // prescripteur inhabituel (valideur exigé)
        var sansDroit = new ForcerAlertesPsychotropes(depot, new DroitsFactices(Permissions.PsychotropesDelivrer));
        var refus = await Assert.ThrowsAsync<DroitRefuseException>(() => sansDroit.ExecuterAsync(4, Delivrance(), "Médecin de garde", null));
        Assert.Empty(depot.Forcages);

        var autorisation = new Securite.AutorisationCourante { AutoriseParId = 1, AutoriseParNom = "Titulaire" };
        var avecCode = new ForcerAlertesPsychotropes(depot, new DroitsFactices(Permissions.PsychotropesDelivrer, Permissions.PsychotropesForcerAlerte),
            autorisation: autorisation);
        await avecCode.ExecuterAsync(4, Delivrance(), "Médecin de garde", null);
        Assert.Equal(1, Assert.Single(depot.Forcages).ValidePar);
    }

    [Fact]
    public async Task Encaissement_refuse_tant_que_l_alerte_n_est_pas_forcee()
    {
        var depot = new DepotFactice { Historique = new(3, 0, 5) };
        var garde = new ControleAlertesPsychotropes(depot, new HorlogeFixe(new DateOnly(2026, 11, 16)));
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => garde.ExigerAsync(4, Delivrance()));
        Assert.Contains("Forcez", e.Message);
        depot.CodesForces.Add(CodesAlertePsychotrope.PatientRepete);
        await garde.ExigerAsync(4, Delivrance());
        // Produit hors registre : aucun contrôle
        await garde.ExigerAsync(4, Delivrance() with { Lignes = [new LignePsychotrope(99, 10)] });
    }

    [Fact]
    public async Task Controle_ne_bloque_jamais_sur_le_repertoire_national()
    {
        var depot = new DepotFactice { Historique = new(0, 0, 5) };
        var controle = new ControlerDelivrancePsychotrope(depot, new DroitsFactices(Permissions.PsychotropesDelivrer), new RepertoireEnPanne());
        var r = await controle.ExecuterAsync(4, Delivrance());
        Assert.Empty(r.Alertes);
        Assert.Contains("injoignable", r.Repertoire);
    }

    [Fact]
    public async Task Annulation_reservee_au_titulaire_avec_motif_et_jamais_deux_fois()
    {
        var depot = new DepotFactice();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new AnnulerEcritureRegistre(depot, new DroitsFactices()).ExecuterAsync(4, 12, "Erreur de lot"));
        var annuler = new AnnulerEcritureRegistre(depot, new DroitsFactices { Titulaire = true });
        await Assert.ThrowsAsync<RegleMetierException>(() => annuler.ExecuterAsync(1, 12, "x"));
        await annuler.ExecuterAsync(1, 12, "Erreur de lot");
        Assert.Equal(120, Assert.Single(depot.Annulees));
        await Assert.ThrowsAsync<RegleMetierException>(() => annuler.ExecuterAsync(1, 12, "Erreur de lot"));
    }

    [Fact]
    public void Classeur_xlsx_valide_avec_texte_echappe()
    {
        var contenu = ClasseurXlsx.Ecrire([new("Registre", [new object?[] { "N°", "Patient" }, new object?[] { 12, "<b>&\"=SOMME(A1)" }])]);
        using var zip = new ZipArchive(new MemoryStream(contenu));
        Assert.NotNull(zip.GetEntry("[Content_Types].xml"));
        Assert.NotNull(zip.GetEntry("xl/workbook.xml"));
        using var lecteur = new StreamReader(zip.GetEntry("xl/worksheets/sheet1.xml")!.Open());
        var xml = lecteur.ReadToEnd();
        Assert.Contains("<v>12</v>", xml);
        Assert.Contains("&lt;b&gt;&amp;&quot;=SOMME(A1)", xml);
        Assert.DoesNotContain("<f>", xml);
        Assert.Equal("AA", ClasseurXlsx.Colonne(26));
    }

    [Fact]
    public void Export_marque_les_ecritures_annulees_et_porte_le_sceau()
    {
        var ligne = new LigneRegistre(12, new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc), 7, "Alprazolam", "L1", "sortie", null, 1, 4, 4,
            "B. K.", "Dr Saïd", "A12", null, "V-1", null, null, null, "Amina");
        var r = new Registre(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30), null, [ligne], [], true,
            Annulations: new Dictionary<long, AnnulationEcriture> { [12] = new(12, new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc), "Erreur de lot", "Titulaire") },
            Sceau: "Intégrité : sceau n° 1");
        var csv = Encoding.UTF8.GetString(RegistreCsv.Ecrire(r));
        Assert.Contains("Annulée le", csv);
        Assert.Contains("Erreur de lot", csv);
        Assert.Contains("Intégrité : sceau n° 1", csv);
        using var zip = new ZipArchive(new MemoryStream(RegistreCsv.Xlsx(r)));
        Assert.Equal(3, zip.Entries.Count(e => e.FullName.StartsWith("xl/worksheets/")));
    }

    [Fact]
    public async Task Verification_signale_une_discordance_du_registre()
    {
        var integrite = new IntegriteFactice();
        var depot = new DepotFactice { Concordance = new ResultatConcordance(10, "ligne du registre modifiée", 5, null, null) };
        var etats = await new VerifierIntegrite(integrite, new Scelleur(), controles: [new ConcordanceRegistre(depot)]).ExecuterAsync(null);
        var e = Assert.Single(etats);
        Assert.Equal(VerifierIntegrite.Rupture, e.Statut);
        Assert.Contains("ligne du registre modifiée", e.Motif);

        depot.Concordance = new ResultatConcordance(10, null, null, null, null);
        Assert.Equal(VerifierIntegrite.Ok, Assert.Single(await new VerifierIntegrite(integrite, new Scelleur(), controles: [new ConcordanceRegistre(depot)]).ExecuterAsync(null)).Statut);
    }

    [Fact]
    public async Task Scellement_mensuel_une_seule_fois_par_mois()
    {
        var integrite = new IntegriteFactice();
        var sceller = new ScellerRegistreMensuel(integrite, new ScellerJournal(integrite, new Scelleur(), new HorlogeFixe(new DateOnly(2026, 12, 1))));
        var s = await sceller.ExecuterAsync(new DateOnly(2026, 12, 1));
        Assert.NotNull(s);
        Assert.Equal(new DateOnly(2026, 11, 1), s.PeriodeDu);
        Assert.Equal(new DateOnly(2026, 11, 30), s.PeriodeAu);
        Assert.Equal(MotifSceau.ClotureMensuelle, s.Motif);
        Assert.Null(await sceller.ExecuterAsync(new DateOnly(2026, 12, 2)));
        Assert.Contains("sceau n° 1 du mois 11/2026", await ScellerRegistreMensuel.TexteSceauAsync(integrite, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)));
    }

    // ---- Doublures ----

    private sealed class RepertoireEnPanne : IRepertoireOrdonnancesConnecteur
    {
        public string Nom => "panne";
        public Task<ReponseRepertoire> SignalerDelivranceAsync(DelivranceRepertoire d, CancellationToken ct = default) =>
            throw new HttpRequestException("injoignable");
    }

    private sealed class Scelleur : IScelleurIntegrite
    {
        private static readonly byte[] Cle = Encoding.ASCII.GetBytes("cle-de-test-de-32-octets-exactem");
        public string Algorithme => "HMAC-SHA256";
        public string EmpreinteCle => "cle-test";
        public byte[] Sceller(byte[] message) => HMACSHA256.HashData(Cle, message);
        public bool Verifier(byte[] message, byte[] sceau) => CryptographicOperations.FixedTimeEquals(Sceller(message), sceau);
    }

    private sealed class IntegriteFactice : IIntegriteDepot
    {
        private static readonly byte[] Hash = new byte[32];
        public List<SceauEnregistre> Sceaux { get; } = [];
        public Task<IReadOnlyList<JournalChaine>> JournauxAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<JournalChaine>>([new JournalChaine(ConcordanceRegistre.Journal, "Registre des psychotropes", "sha256", DateTime.UtcNow)]);
        public Task<IReadOnlyList<ResultatChaine>> VerifierChaineAsync(string journal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ResultatChaine>>([new ResultatChaine("officine", 3, 3, Hash, null, null, null, null, null)]);
        public Task<MaillonChaine> DernierMaillonAsync(string journal, string cleChaine, CancellationToken ct = default) =>
            Task.FromResult(new MaillonChaine(3, 30, Hash));
        public Task<byte[]?> HashAuRangAsync(string journal, string cleChaine, long rang, CancellationToken ct = default) => Task.FromResult<byte[]?>(Hash);
        public Task<long> EnregistrerSceauAsync(NouveauSceau s, CancellationToken ct = default)
        {
            Sceaux.Add(new SceauEnregistre(Sceaux.Count + 1, s.Journal, s.CleChaine, s.Motif, s.PeriodeDu, s.PeriodeAu, s.ScelleLeUtc, s.DernierRang,
                s.DernierId, s.DernierHash, s.Algorithme, s.EmpreinteCle, s.Sceau, s.AuteurId));
            return Task.FromResult((long)Sceaux.Count);
        }
        public Task<IReadOnlyList<SceauEnregistre>> SceauxAsync(string journal, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SceauEnregistre>>(Sceaux.ToList());
        public Task EnregistrerVerificationAsync(NouvelleVerification v, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DerniereVerification>> DernieresVerificationsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<DerniereVerification>>([]);
        public Task<ParametresIntegrite> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresIntegrite("sha256", 3, "hex"));
        public Task JournaliserRuptureSystemeAsync(string journal, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DepotFactice : IRegistreRenforceDepot
    {
        public HistoriquePsychotrope Historique { get; set; } = new(0, 0, 5);
        public HashSet<string> CodesForces { get; } = [];
        public List<NouveauForcage> Forcages { get; } = [];
        public List<long> Annulees { get; } = [];
        public ResultatConcordance Concordance { get; set; } = new(0, null, null, null, null);

        public Task<IReadOnlyList<RegleAlertePsychotrope>> ReglesAsync(CancellationToken ct = default) => Task.FromResult(Regles);
        public Task EnregistrerReglesAsync(IReadOnlyList<RegleAlertePsychotrope> regles, long auteurId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyDictionary<long, string>> ProduitsSousRegistreAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, string>>(Produits.Where(p => produitIds.Contains(p.Key)).ToDictionary());
        public Task<HistoriquePsychotrope> HistoriqueAsync(DelivrancePsychotrope d, IReadOnlyList<RegleAlertePsychotrope> regles, CancellationToken ct = default) =>
            Task.FromResult(Historique);
        public Task<long> EnregistrerForcageAsync(NouveauForcage f, CancellationToken ct = default) { Forcages.Add(f); return Task.FromResult((long)Forcages.Count); }
        public Task<IReadOnlySet<string>> CodesForcesAsync(long utilisateurId, DelivrancePsychotrope d, DateTime depuisUtc, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(CodesForces);
        public Task<Page<ForcageAlerte>> ForcagesAsync(DateOnly du, DateOnly au, string? code, DemandePage page, CancellationToken ct = default) =>
            Task.FromResult(new Page<ForcageAlerte>([], 0, 1, 20));
        public Task<LigneRegistreAnnulable?> LigneAsync(long numero, CancellationToken ct = default) =>
            Task.FromResult<LigneRegistreAnnulable?>(numero == 12 ? new(120, 12, "Alprazolam", "sortie", DateTime.UtcNow, Annulees.Contains(120)) : null);
        public Task<long> AnnulerAsync(long registreId, string motif, long utilisateurId, CancellationToken ct = default) { Annulees.Add(registreId); return Task.FromResult(1L); }
        public Task<IReadOnlyDictionary<long, AnnulationEcriture>> AnnulationsAsync(IReadOnlyCollection<long> numeros, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, AnnulationEcriture>>(new Dictionary<long, AnnulationEcriture>());
        public Task<ResultatConcordance> ConcordanceAsync(CancellationToken ct = default) => Task.FromResult(Concordance);
        public Task<IReadOnlyList<ClasseSuivie>> ClassesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ClasseSuivie>>([]);
        public Task<IReadOnlyList<DciClasse>> DciClasseAsync(string classe, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DciClasse>>([]);
        public Task<IReadOnlyList<DciClasse>> ChercherDciAsync(string texte, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DciClasse>>([]);
        public Task AjouterDciAsync(string classe, long dciId, long auteurId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RetirerDciAsync(string classe, long dciId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<LigneSuiviClasse>> SuiviClasseAsync(string classe, DateOnly du, DateOnly au, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<LigneSuiviClasse>>([]);
    }
}
