using Pharma.Application.Caisse;
using Pharma.Application.Patients;
using Pharma.Application.Psychotropes;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Wilaya du médecin, zone de l'officine (alerte « hors zone ») et durée du traitement (script 118, 2026-11-16).</summary>
public class RegistreWilayaDureeTests
{
    private static readonly DateOnly Jour = new(2026, 11, 16);

    private static readonly IReadOnlyList<RegleAlertePsychotrope> Regles =
    [
        new(CodesAlertePsychotrope.QuantiteAnormale, "Quantité", true, 3, 28, false, UnitesParJour: 3),
        new(CodesAlertePsychotrope.PrescripteurHorsZone, "Hors zone", true, 1, 1, false)
    ];

    private static readonly Dictionary<long, string> Produits = new() { [7] = "Alprazolam" };
    private static readonly HashSet<string> Zone = ["16", "09", "35"];

    private static DelivrancePsychotrope Delivrance(int boites = 1, int? duree = null, long? medecin = 3) =>
        new(1, medecin, "A12", Jour, [new LignePsychotrope(7, boites)], duree);

    private static HistoriquePsychotrope Faits(string? wilaya, IReadOnlySet<string>? zone = null, int? contenance = null) =>
        new(0, 0, 5, wilaya, zone ?? Zone, contenance is { } c ? new Dictionary<long, int> { [7] = c } : null);

    [Fact]
    public void Medecin_hors_zone_declenche_l_alerte()
    {
        var alertes = EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(), Faits("31"), Produits);
        var a = Assert.Single(alertes);
        Assert.Equal(CodesAlertePsychotrope.PrescripteurHorsZone, a.Code);
        Assert.Contains("31", a.Message);
    }

    [Fact]
    public void Medecin_dans_la_zone_ne_declenche_rien()
    {
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(), Faits("09"), Produits));
        Assert.Empty(EvaluationAlertesPsychotropes.Avis(Delivrance(), Faits("09")));
    }

    [Fact]
    public void Medecin_sans_wilaya_pas_d_alerte_mais_signale()
    {
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(), Faits(null), Produits));
        var avis = Assert.Single(EvaluationAlertesPsychotropes.Avis(Delivrance(), Faits(null)));
        Assert.Contains("Wilaya du prescripteur à renseigner", avis);
    }

    [Fact]
    public void Zone_non_reglee_pas_de_controle_hors_zone()
    {
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(), Faits("31", new HashSet<string>()), Produits));
    }

    [Fact]
    public async Task Controle_renvoie_l_avis_wilaya_sans_alerte()
    {
        var depot = new DepotHistorique(Faits(null));
        var r = await new ControlerDelivrancePsychotrope(depot, new DroitsFactices(Pharma.Domain.Securite.Permissions.PsychotropesDelivrer))
            .ExecuterAsync(4, Delivrance());
        Assert.Empty(r.Alertes);
        Assert.Single(r.Avis!);
    }

    [Fact]
    public void Quantite_anormale_boites_fois_contenance_comparees_a_la_duree()
    {
        // 2 boîtes × 30 = 60 unités pour 10 jours à 3 unités par jour (30 au plus) : alerte
        var alerte = Assert.Single(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(boites: 2, duree: 10), Faits("16", contenance: 30), Produits));
        Assert.Equal(CodesAlertePsychotrope.QuantiteAnormale, alerte.Code);
        Assert.Contains("2 × 30 unités", alerte.Message);
        // 20 jours : 60 unités = 20 × 3, pas d'alerte
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(boites: 2, duree: 20), Faits("16", contenance: 30), Produits));
        // Contenance inconnue : pas de contrôle
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(boites: 2, duree: 10), Faits("16"), Produits));
    }

    [Fact]
    public void Duree_absente_ne_declenche_rien()
    {
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer(Regles, Delivrance(boites: 2, duree: null), Faits("16", contenance: 30), Produits));
    }

    // ---- Caisse : durée obligatoire pour un psychotrope (script 153, décision du titulaire du 2026-11-19) ----

    private static (Encaisser Cas, VentesEspion Ventes) Caisse()
    {
        var ventes = new VentesEspion();
        return (new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(),
            new DroitsFactices("caisse.vendre", "psychotropes.delivrer"), new HorlogeFixe(Jour), new ScansEnMemoire()), ventes);
    }

    private static CommandeVente Commande(long produit, long lot, int? duree) =>
        new([new LigneCommande(produit, lot, 1)], false, null, null, PatientId: 3,
            new OrdonnanceSaisie(1, Jour, "ORD-1", DureeTraitementJours: duree), 0, ModePaiement.Especes, 100_000m);

    [Fact]
    public async Task Duree_absente_refusee_pour_un_psychotrope_acceptee_hors_psychotrope()
    {
        var (cas, ventes) = Caisse();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Commande(7, 70, null), 5, "Caisse 1"));
        Assert.Equal("duree_traitement", ex.Champ);
        Assert.Contains("durée du traitement", ex.Message);
        Assert.Null(ventes.Recu);
        await cas.ExecuterAsync(Commande(2, 20, null), 5, "Caisse 1");
        Assert.Null(ventes.Recu!.Ordonnance!.DureeTraitementJours);
    }

    [Fact]
    public async Task Duree_transmise_au_registre_pour_un_psychotrope()
    {
        var (cas, ventes) = Caisse();
        await cas.ExecuterAsync(Commande(7, 70, 30), 5, "Caisse 1");
        Assert.Equal(30, ventes.Recu!.Ordonnance!.DureeTraitementJours);
    }

    [Fact]
    public async Task Duree_hors_bornes_refusee_et_ignoree_hors_psychotrope()
    {
        var (cas, ventes) = Caisse();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Commande(7, 70, 0), 5, "Caisse 1"));
        Assert.Equal("duree_traitement", ex.Champ);
        await cas.ExecuterAsync(Commande(2, 20, 30), 5, "Caisse 1");
        Assert.Null(ventes.Recu!.Ordonnance!.DureeTraitementJours);
    }

    // ---- Script 153 : patient encore sous traitement, ordonnance réutilisée sans limite ----

    private static readonly RegleAlertePsychotrope EnCours = new(CodesAlertePsychotrope.TraitementEnCours, "En cours", true, 1, null, true);

    [Fact]
    public void Patient_encore_sous_traitement_declenche_l_alerte_avec_valideur_et_sans_nom()
    {
        // Délivré le 10/11 pour 14 jours : traitement jusqu'au 23/11 inclus
        var faits = Faits("16") with { EnCours = new TraitementPsychotropeEnCours(new DateOnly(2026, 11, 10), 14, new DateOnly(2026, 11, 24)) };
        var a = Assert.Single(EvaluationAlertesPsychotropes.Evaluer([EnCours], Delivrance(duree: 10), faits, Produits));
        Assert.Equal(CodesAlertePsychotrope.TraitementEnCours, a.Code);
        Assert.True(a.ExigeValideur);
        Assert.Equal("Patient encore sous traitement psychotrope jusqu’au 23/11/2026 (délivrance du 10/11/2026, 14 jours).", a.Message);
    }

    [Fact]
    public void Aucun_traitement_en_cours_ou_regle_inactive_ou_sans_patient_pas_d_alerte()
    {
        var faits = Faits("16") with { EnCours = new TraitementPsychotropeEnCours(Jour, 7, Jour.AddDays(7)) };
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer([EnCours], Delivrance(duree: 10), Faits("16"), Produits));
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer([EnCours with { Active = false }], Delivrance(duree: 10), faits, Produits));
        Assert.Empty(EvaluationAlertesPsychotropes.Evaluer([EnCours], Delivrance(duree: 10) with { PatientId = null }, faits, Produits));
    }

    [Fact]
    public void Ordonnance_reutilisee_sans_limite_de_duree()
    {
        var regle = new RegleAlertePsychotrope(CodesAlertePsychotrope.OrdonnanceReutilisee, "Ordonnance", true, 1, null, false);
        var a = Assert.Single(EvaluationAlertesPsychotropes.Evaluer([regle], Delivrance(), new HistoriquePsychotrope(0, 1, 5), Produits));
        Assert.Equal("Ordonnance n° A12 du même prescripteur déjà utilisée 1 fois.", a.Message);
        Assert.Contains("sur 365 jours", Assert.Single(EvaluationAlertesPsychotropes.Evaluer([regle with { PeriodeJours = 365 }], Delivrance(),
            new HistoriquePsychotrope(0, 1, 5), Produits)).Message);
    }

    // ---- Wilaya : saisie du médecin et zone ----

    [Fact]
    public void Wilaya_du_medecin_normalisee_facultative_et_bornee()
    {
        Assert.Equal("09", new SaisieMedecin("Dr Fictif", null, Wilaya: "9").Verifier().Wilaya);
        Assert.Null(new SaisieMedecin("Dr Fictif", null).Verifier().Wilaya);
        Assert.Equal("58", RegleWilaya.Normaliser("58"));
        Assert.Throws<RegleMetierException>(() => new SaisieMedecin("Dr Fictif", null, Wilaya: "70").Verifier());
        Assert.Throws<RegleMetierException>(() => RegleWilaya.Normaliser("00"));
        Assert.Throws<RegleMetierException>(() => RegleWilaya.Normaliser("1a"));
    }

    [Fact]
    public void Wilayas_59_a_69_acceptees_70_refusee_liste_a_69()
    {
        Assert.Equal(69, RegleWilaya.Max);
        Assert.Equal("59", new SaisieMedecin("Dr Fictif", null, Wilaya: "59").Verifier().Wilaya);
        Assert.Equal("69", RegleWilaya.Normaliser("69"));
        Assert.Throws<RegleMetierException>(() => RegleWilaya.Normaliser("70"));
        var liste = Enumerable.Range(1, 99).Select(n => n.ToString("00"))
            .Where(c => { try { return RegleWilaya.Normaliser(c) is not null; } catch (RegleMetierException) { return false; } }).ToList();
        Assert.Equal(69, liste.Count);
        Assert.Equal("69", liste[^1]);
    }

    [Fact]
    public async Task Zone_officine_reglee_par_le_titulaire_sans_doublon()
    {
        var depot = new ZoneEnMemoire();
        await Assert.ThrowsAsync<Pharma.Application.Commun.DroitRefuseException>(() =>
            new ParametrerZonePsychotropes(depot, new DroitsFactices()).EnregistrerAsync(1, new ZoneOfficine("16", [])));
        var cas = new ParametrerZonePsychotropes(depot, new DroitsFactices { Titulaire = true });
        await cas.EnregistrerAsync(1, new ZoneOfficine("16", ["9", "16", "35", "09"]));
        Assert.Equal("16", depot.Zone.Officine);
        Assert.Equal(["09", "35"], depot.Zone.Voisines);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.EnregistrerAsync(1, new ZoneOfficine(null, ["09"])));
    }

    private sealed class ZoneEnMemoire : IZonePsychotropesDepot
    {
        public ZoneOfficine Zone { get; private set; } = new(null, []);
        public Task<IReadOnlyList<Wilaya>> WilayasAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Wilaya>>([]);
        public Task<ZoneOfficine> ZoneAsync(CancellationToken ct = default) => Task.FromResult(Zone);
        public Task EnregistrerZoneAsync(ZoneOfficine zone, long auteurId, CancellationToken ct = default) { Zone = zone; return Task.CompletedTask; }
    }

    /// <summary>Dépôt réduit : règles ci-dessus, produit 7 sous registre, faits donnés.</summary>
    private sealed class DepotHistorique(HistoriquePsychotrope faits) : IRegistreRenforceDepot
    {
        public Task<IReadOnlyList<RegleAlertePsychotrope>> ReglesAsync(CancellationToken ct = default) => Task.FromResult(Regles);
        public Task<IReadOnlyDictionary<long, string>> ProduitsSousRegistreAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<long, string>>(Produits);
        public Task<HistoriquePsychotrope> HistoriqueAsync(DelivrancePsychotrope d, IReadOnlyList<RegleAlertePsychotrope> regles, CancellationToken ct = default) =>
            Task.FromResult(faits);
        public Task EnregistrerReglesAsync(IReadOnlyList<RegleAlertePsychotrope> regles, long auteurId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> EnregistrerForcageAsync(NouveauForcage f, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlySet<string>> CodesForcesAsync(long utilisateurId, DelivrancePsychotrope d, DateTime depuisUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Pharma.Application.Commun.Page<ForcageAlerte>> ForcagesAsync(DateOnly du, DateOnly au, string? code, Pharma.Application.Commun.DemandePage page, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<LigneRegistreAnnulable?> LigneAsync(long numero, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<long> AnnulerAsync(long registreId, string motif, long utilisateurId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<long, AnnulationEcriture>> AnnulationsAsync(IReadOnlyCollection<long> numeros, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ResultatConcordance> ConcordanceAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ClasseSuivie>> ClassesAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DciClasse>> DciClasseAsync(string classe, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<DciClasse>> ChercherDciAsync(string texte, CancellationToken ct = default) => throw new NotSupportedException();
        public Task AjouterDciAsync(string classe, long dciId, long auteurId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RetirerDciAsync(string classe, long dciId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LigneSuiviClasse>> SuiviClasseAsync(string classe, DateOnly du, DateOnly au, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
