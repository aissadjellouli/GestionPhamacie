using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Domain.Tests;

public class RegleFactureTests
{
    // Vente tiers payant fictive : 2 produits, remise de 14 DA sur la part patient
    private static readonly LigneVendue[] Vente =
    [
        new(1, "T-000003", 10, "Produit A", 0, 2, 100, 400, 14),
        new(1, "T-000003", 11, "Produit B", 0, 1, 40, 160, 14)
    ];

    [Fact]
    public void Facture_patient_reprend_la_part_patient_et_la_remise()
    {
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, Vente);
        Assert.Equal([100m, 40m, -14m], lignes.Select(l => l.MontantTtc));
        Assert.All(lignes, l => Assert.Equal("patient", l.Part));
        Assert.Equal(126m, RegleFacture.Calculer(lignes).Ttc);
    }

    [Fact]
    public void La_remise_porte_la_tva_des_produits_remises()
    {
        // Produit à 19 % vendu 119 DA TTC avec 19 DA de remise : TTC 100, HT 84,03, TVA 15,97 (et non TVA 19)
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, [new LigneVendue(2, "T-000004", 20, "Parapharmacie", 19, 1, 119, 0, 19)]);
        Assert.Equal([(119m, 19m), (-19m, 19m)], lignes.Select(l => (l.MontantTtc, l.TauxTva)));
        var t = RegleFacture.Calculer(lignes);
        Assert.Equal((84.03m, 15.97m, 100m), (t.Ht, t.Tva, t.Ttc));
    }

    [Fact]
    public void Vente_anterieure_aux_reductions_par_ligne_la_remise_est_repartie_entre_les_taux_de_tva_au_prorata()
    {
        // Ventes enregistrées avant le 2026-11-14 (aucune réduction par ligne) : ancienne répartition conservée
        // 300 DA à 0 % et 100 DA à 19 % : la remise de 40 DA se partage 30 / 10, sans perdre un centime
        LigneVendue[] vente = [new(3, "T-000005", 30, "Médicament", 0, 1, 300, 0, 40), new(3, "T-000005", 31, "Parapharmacie", 19, 1, 100, 0, 40)];
        var remises = RegleFacture.DepuisVentes(Destinataire.Patient, vente).Where(l => l.MontantTtc < 0).ToList();
        Assert.Equal([(-30m, 0m), (-10m, 19m)], remises.Select(l => (l.MontantTtc, l.TauxTva)));
        Assert.Equal(360m, RegleFacture.Calculer(RegleFacture.DepuisVentes(Destinataire.Patient, vente)).Ttc);
    }

    [Fact]
    public void Reductions_par_ligne_la_remise_reste_sur_le_produit_remise()
    {
        // 300 DA à 0 % sans remise, 119 DA à 19 % remisé de 19 DA sur la ligne : la TVA ne baisse que sur le produit remisé
        LigneVendue[] vente =
        [
            new(4, "T-000006", 40, "Médicament", 0, 1, 300, 0, 19, 0, 0),
            new(4, "T-000006", 41, "Parapharmacie", 19, 1, 119, 0, 19, 0, 19)
        ];
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, vente);
        Assert.Equal([(300m, 0m), (100m, 19m)], lignes.Select(l => (l.MontantTtc, l.TauxTva)));
        Assert.DoesNotContain(lignes, l => l.MontantTtc < 0);
        var t = RegleFacture.Calculer(lignes);
        Assert.Equal((384.03m, 15.97m, 400m), (t.Ht, t.Tva, t.Ttc));
    }

    [Fact]
    public void Reductions_par_ligne_don_partiel_et_plusieurs_taux()
    {
        // Don partiel de 50 DA sur un médicament à 0 %, remise de 10 DA sur un produit à 9 %, produit à 19 % sans réduction
        LigneVendue[] vente =
        [
            new(5, "T-000007", 50, "Médicament", 0, 2, 200, 300, 0, 50, 50),
            new(5, "T-000007", 51, "Produit 9", 9, 1, 109, 0, 10, 50, 10),
            new(5, "T-000007", 52, "Produit 19", 19, 1, 238, 0, 10, 50, 0)
        ];
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, vente);
        Assert.Equal([(150m, 0m, 75m), (99m, 9m, 99m), (238m, 19m, 238m)], lignes.Select(l => (l.MontantTtc, l.TauxTva, l.PrixUnitaireTtc)));
        var t = RegleFacture.Calculer(lignes);
        // HT : 150 + 90,83 + 200 ; TTC = ce que le patient a payé (547 - 60)
        Assert.Equal((440.83m, 46.17m, 487m), (t.Ht, t.Tva, t.Ttc));
        // La part organisme n'est pas touchée par les réductions du patient
        Assert.Equal([300m], RegleFacture.DepuisVentes(Destinataire.Organisme, vente).Select(l => l.MontantTtc));
    }

    [Fact]
    public void Reductions_par_ligne_apres_retour_le_ttc_reste_le_paye()
    {
        // 2 boîtes à 119 DA remisées de 20 DA en tout, une rendue : quantité nette 1, part 119, réduction au prorata 10
        var lignes = RegleFacture.DepuisVentes(Destinataire.Patient, [new LigneVendue(6, "T-000008", 60, "Parapharmacie", 19, 1, 119, 0, 20, 0, 10)]);
        Assert.Equal(109m, RegleFacture.Calculer(lignes).Ttc);
    }

    [Fact]
    public void Facture_organisme_reprend_la_part_prise_en_charge_sans_remise()
    {
        var lignes = RegleFacture.DepuisVentes(Destinataire.Organisme, Vente);
        Assert.Equal([400m, 160m], lignes.Select(l => l.MontantTtc));
        Assert.Equal(200m, lignes[0].PrixUnitaireTtc);
    }

    [Fact]
    public void Tva_calculee_depuis_le_prix_ttc()
    {
        var t = RegleFacture.Calculer([RegleFacture.Libre("Article parapharmacie", 1, 1190, 19), RegleFacture.Libre("Médicament", 2, 100, 0)]);
        Assert.Equal((1200m, 190m, 1390m), (t.Ht, t.Tva, t.Ttc));
    }

    [Fact]
    public void Numeros_annuels_factures_et_avoirs()
    {
        Assert.Equal("F-2026-0007", RegleFacture.Numero(false, 2026, 7));
        Assert.Equal("AV-2026-0001", RegleFacture.Numero(true, 2026, 1));
    }

    [Fact]
    public void Rejet_correction_avoir_et_suivi_controles()
    {
        // Levée du rejet : facture rejetée seulement, motif obligatoire
        RegleFacture.VerifierLeveeRejet("facture", "rejetee", "Organisme revenu sur son rejet");
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierLeveeRejet("facture", "emise", "x"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierLeveeRejet("avoir", "rejetee", "x"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierLeveeRejet("facture", "rejetee", " "));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierRejet("facture", "emise", " "));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierRejet("facture", "rejetee", "Erreur de taux"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierReprise("emise"));
        RegleFacture.VerifierReprise("rejetee");
        // Rejetée puis payée en entier : soldée, plus de correction ni de remplacement
        RegleFacture.VerifierReprise("rejetee", 100);
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierReprise("rejetee", 0));
        Assert.True(RegleFacture.RejetLeve("rejetee", 0, horsSuivi: false));
        Assert.False(RegleFacture.RejetLeve("rejetee", 50, horsSuivi: false));
        Assert.False(RegleFacture.RejetLeve("emise", 0, horsSuivi: false));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "emise", 1000, 800, 300, "Retour"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "remplacee", 1000, 0, 100, "Retour"));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierHorsSuivi(true, null));
        RegleFacture.VerifierHorsSuivi(false, null);
    }

    [Fact]
    public void Ligne_libre_et_facture_vide_refusees()
    {
        Assert.Throws<RegleMetierException>(() => RegleFacture.Libre(" ", 1, 10, 0));
        Assert.Throws<RegleMetierException>(() => RegleFacture.Libre("Frais", 0, 10, 0));
        Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierEmission([]));
    }

    [Fact]
    public void Reglement_impute_dans_l_ordre_au_plus_le_reste() =>
        Assert.Equal([(5L, 300m), (6L, 200m)], RegleFacture.Imputer(500, [(5L, 300m), (6L, 450m), (7L, 100m)]));

    [Theory]
    [InlineData(0, "Zéro dinar")]
    [InlineData(1, "Un dinar")]
    [InlineData(71, "Soixante et onze dinars")]
    [InlineData(80, "Quatre-vingts dinars")]
    [InlineData(91, "Quatre-vingt-onze dinars")]
    [InlineData(200, "Deux cents dinars")]
    [InlineData(1250.5, "Mille deux cent cinquante dinars et cinquante centimes")]
    [InlineData(80000, "Quatre-vingt mille dinars")]
    [InlineData(2_300_021, "Deux millions trois cent mille vingt et un dinars")]
    [InlineData(200_000_000, "Deux cents millions de dinars")]
    [InlineData(80_000_000, "Quatre-vingts millions de dinars")]
    [InlineData(280_000_000, "Deux cent quatre-vingts millions de dinars")]
    [InlineData(2_000_000_000, "Deux milliards de dinars")]
    [InlineData(200_000_000_000, "Deux cents milliards de dinars")]
    [InlineData(1_000_000, "Un million de dinars")]
    [InlineData(2_500_000, "Deux millions cinq cent mille dinars")]
    [InlineData(2_000_000.50, "Deux millions de dinars et cinquante centimes")]
    [InlineData(3_000_000_007, "Trois milliards sept dinars")]
    [InlineData(200_000, "Deux cent mille dinars")]
    [InlineData(0.125, "Zéro dinar et treize centimes")]   // arrondi au centime : la moitié vers le haut
    public void Montant_en_lettres(decimal montant, string attendu) => Assert.Equal(attendu, MontantEnLettres.Dinars(montant));

    [Theory]
    [InlineData(2.345, 2.35)]
    [InlineData(2.335, 2.34)]
    [InlineData(0.005, 0.01)]
    [InlineData(-2.345, -2.35)]
    [InlineData(10, 10)]
    public void Arrondi_au_centime_la_moitie_vers_le_haut(decimal montant, decimal attendu) => Assert.Equal(attendu, Arrondi.Centime(montant));

    [Fact]
    public void Ligne_libre_arrondie_la_moitie_vers_le_haut()
    {
        var l = RegleFacture.Libre("Frais", 1, 2.345m, 0);
        Assert.Equal((2.35m, 2.35m), (l.PrixUnitaireTtc, l.MontantTtc));   // l'arrondi bancaire donnerait 2,34
    }

    [Theory]
    [InlineData(100.005)]
    [InlineData(0.001)]
    public void Avoir_a_plus_de_deux_decimales_refuse(decimal montant)
    {
        var ex = Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "emise", 1000, 0, montant, "Retour"));
        Assert.Contains("deux décimales", ex.Message);
    }

    [Fact]
    public void Avoir_au_centime_accepte() => RegleFacture.VerifierAvoir("facture", "emise", 1000, 0, 100.05m, "Retour");

    // ---- Avoir sur une facture passée en dette du patient (084) ----

    // Reste de la facture tel que v_facture_client le calcule : l'avoir n'en retire que (montant - part_credit), la part imputée
    // sur la dette y figurant déjà comme réglée sur les ventes à crédit.
    private static decimal ResteVue(decimal ttc, decimal avoir, decimal partCredit, decimal regleCredit) =>
        Math.Max(ttc - (avoir - partCredit) - regleCredit, 0);

    [Fact]
    public void Avoir_de_20_sur_120_a_credit_reste_100_et_dette_100_sans_double_compte()
    {
        var parts = RegleFacture.RepartirAvoirSurDette(20, [(7, 120m)]);
        var partCredit = parts.Sum(p => p.Montant);
        Assert.Equal([(7L, 20m)], parts);
        var dette = 120m - partCredit;
        Assert.Equal(100m, dette);
        // La dette baisse de 20 : 20 apparaissent réglés sur la vente à crédit
        Assert.Equal(100m, ResteVue(120, 20, partCredit, regleCredit: 120 - dette));
    }

    [Fact]
    public void Detail_facture_apres_avoir_les_lignes_s_additionnent()
    {
        // F-2026-0025 : 690 à crédit, 100 réglés par le patient, avoir de 50 imputé sur la dette (réglé à crédit vu : 150)
        var partCredit = RegleFacture.RepartirAvoirSurDette(50, [(7, 590m)]).Sum(p => p.Montant);
        var regleAffiche = RegleFacture.RegleParDetteAffiche(150m, partCredit);
        var reste = ResteVue(690, 50, partCredit, regleCredit: 150);
        Assert.Equal(100m, regleAffiche);
        Assert.Equal(540m, reste);
        Assert.Equal(reste, 690m - regleAffiche - 50m);
    }

    [Fact]
    public void Liste_factures_paye_aligne_sur_le_detail_apres_avoir()
    {
        // E6 : F-2026-0025, liste « Payé / réglé » 150 (avoir compté) ; le détail dit déjà réglé 100 et avoirs −50
        var partCredit = RegleFacture.RepartirAvoirSurDette(50, [(7, 590m)]).Sum(p => p.Montant);
        var reste = ResteVue(690, 50, partCredit, regleCredit: 150);
        var payeListe = RegleFacture.PayeAfficheListe(690m, reste, 50m);
        Assert.Equal(100m, payeListe);
        Assert.Equal(RegleFacture.RegleParDetteAffiche(150m, partCredit), payeListe);
    }

    [Fact]
    public void Liste_factures_paye_sans_avoir_inchange() =>
        Assert.Equal(200m, RegleFacture.PayeAfficheListe(500m, 300m, 0m));

    [Fact]
    public void Liste_factures_paye_jamais_negatif() =>
        Assert.Equal(0m, RegleFacture.PayeAfficheListe(100m, 0m, 120m));

    [Fact]
    public void Detail_facture_sans_avoir_regle_inchange() =>
        Assert.Equal(120m, RegleFacture.RegleParDetteAffiche(120m, 0m));

    [Fact]
    public void Avoir_sur_facture_deja_payee_aucun_mouvement_de_dette() =>
        Assert.Empty(RegleFacture.RepartirAvoirSurDette(20, [(7, 0m)]));

    [Fact]
    public void Avoir_sans_vente_a_credit_aucun_mouvement_de_dette() =>
        Assert.Empty(RegleFacture.RepartirAvoirSurDette(20, []));

    [Fact]
    public void Avoir_superieur_au_reste_a_credit_plafonne()
    {
        // 120 à crédit dont 100 déjà remboursés : il reste 20 ; avoir de 50 → la dette ne baisse que de 20
        var parts = RegleFacture.RepartirAvoirSurDette(50, [(7, 20m)]);
        Assert.Equal(20m, parts.Sum(p => p.Montant));
    }

    [Fact]
    public void Avoir_reparti_sur_les_ventes_les_plus_anciennes_d_abord()
    {
        var parts = RegleFacture.RepartirAvoirSurDette(30, [(1, 10m), (2, 50m), (3, 40m)]);
        Assert.Equal([(1L, 10m), (2L, 20m)], parts);
    }
}
