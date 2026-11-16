using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Imputation des règlements de crédit vente par vente (087, règle du titulaire du 2026-11-15).</summary>
public class ImputationCreditTests
{
    // V0 : 100 à crédit, plus ancienne, non facturée ; V1 : 120 à crédit, sur la facture F
    private const long V0 = 1, V1 = 2;

    /// <summary>Reste de F comme v_facture_client : TTC − (avoirs − part_credit) − (règlements − part_credit) − réglé de ses ventes.</summary>
    private static decimal ResteFacture(decimal ttc, decimal avoir, decimal partCreditAvoir, decimal reglement, decimal partCreditReglement, decimal regleVentes) =>
        Math.Max(ttc - (avoir - partCreditAvoir) - (reglement - partCreditReglement) - regleVentes, 0);

    private static (decimal Regle, decimal Reste) Vente(IReadOnlyList<(long VenteId, decimal Regle, decimal Reste)> r, long id) =>
        r.Where(v => v.VenteId == id).Select(v => (v.Regle, v.Reste)).Single();

    [Fact]
    public void Avoir_sur_facture_solde_la_vente_de_la_facture_pas_la_plus_ancienne()
    {
        // Avoir de 20 sur F : imputé sur V1 (vente de F), dette 220 → 200
        var parts = RegleFacture.RepartirAvoirSurDette(20, [(V1, 120m)]);
        Assert.Equal([(V1, 20m)], parts);
        var r = RegleImputationCredit.Calculer(0, 200, [new(V0, 100, 0), new(V1, 120, 20)]);
        Assert.Equal((0m, 100m), Vente(r, V0));
        Assert.Equal((20m, 100m), Vente(r, V1));
        Assert.Equal(100m, ResteFacture(120, 20, 20, 0, 0, Vente(r, V1).Regle));
        Assert.Equal(200m, r.Sum(v => v.Reste));
    }

    [Fact]
    public void Sans_imputation_l_avoir_aurait_solde_V0_defaut_corrige()
    {
        // Ancien calcul (tout en reliquat) : V0 réglée de 20, F reste 120 ; c'est le défaut corrigé par 087
        var r = RegleImputationCredit.Calculer(0, 200, [new(V0, 100, 0), new(V1, 120, 0)]);
        Assert.Equal(20m, Vente(r, V0).Regle);
        Assert.Equal(120m, ResteFacture(120, 20, 20, 0, 0, Vente(r, V1).Regle));
    }

    [Fact]
    public void Reglement_de_facture_en_tresorerie_solde_la_vente_de_la_facture()
    {
        // F encaissée de 20 en trésorerie : part_credit 20 imputée sur V1, dette 200
        var r = RegleImputationCredit.Calculer(0, 200, [new(V0, 100, 0), new(V1, 120, 20)]);
        Assert.Equal(100m, Vente(r, V0).Reste);
        Assert.Equal(100m, ResteFacture(120, 0, 0, 20, 20, Vente(r, V1).Regle));
    }

    [Fact]
    public void Reglement_au_comptoir_impute_sur_les_ventes_cochees()
    {
        // Le patient choisit V1 seule : 50 réglés sur V1, V0 reste due en entier
        var parts = RegleReglementCredit.Imputer(50, [(V1, 120m)]);
        Assert.Equal([(V1, 50m)], parts);
        var r = RegleImputationCredit.Calculer(0, 170, [new(V0, 100, 0), new(V1, 120, 50)]);
        Assert.Equal((0m, 100m), Vente(r, V0));
        Assert.Equal((50m, 70m), Vente(r, V1));
    }

    [Fact]
    public void Reglement_au_comptoir_surplus_non_rattache_solde_la_plus_ancienne()
    {
        // 150 réglés, seule V1 cochée (reste 120) : 120 sur V1, les 30 restants vont à la plus ancienne (V0)
        var parts = RegleReglementCredit.Imputer(150, [(V1, 120m)]);
        Assert.Equal([(V1, 120m)], parts);
        var r = RegleImputationCredit.Calculer(0, 70, [new(V0, 100, 0), new(V1, 120, 120)]);
        Assert.Equal((30m, 70m), Vente(r, V0));
        Assert.Equal((120m, 0m), Vente(r, V1));
    }

    [Fact]
    public void Choix_par_defaut_les_plus_anciennes_jusqu_au_montant()
    {
        Assert.Equal([V0], RegleReglementCredit.VentesParDefaut(80, [(V0, 100m), (V1, 120m)]));
        Assert.Equal([V0, V1], RegleReglementCredit.VentesParDefaut(150, [(V0, 100m), (V1, 120m)]));
        Assert.Equal([V0], RegleReglementCredit.VentesParDefaut(100, [(V0, 100m), (V1, 120m)]));
        // Vente déjà soldée jamais cochée
        Assert.Equal([V1], RegleReglementCredit.VentesParDefaut(50, [(V0, 0m), (V1, 120m)]));
        Assert.Empty(RegleReglementCredit.VentesParDefaut(0, [(V0, 100m)]));
    }

    [Fact]
    public void Surplus_annonce_avec_la_dette_qu_il_soldera()
    {
        // V1 cochée (reste 120), 150 encaissés : 30 de surplus pour V0, la plus ancienne non cochée
        Assert.Equal((30m, false, (long?)V0), RegleReglementCredit.Surplus(150, [V1], [(V0, 100m), (V1, 120m)], 0));
        // Dette reprise de l'ancien logiciel encore due : soldée en premier
        Assert.Equal((30m, true, (long?)null), RegleReglementCredit.Surplus(150, [V1], [(V0, 100m), (V1, 120m)], 40));
        // Montant couvert par les ventes cochées : pas de surplus
        Assert.Equal((0m, false, (long?)null), RegleReglementCredit.Surplus(120, [V1], [(V0, 100m), (V1, 120m)], 0));
        // Aucune vente cochée : le montant solde le plus ancien, sans message de surplus
        Assert.Equal((0m, false, (long?)null), RegleReglementCredit.Surplus(150, [], [(V0, 100m), (V1, 120m)], 0));
        // Vente cochée déjà soldée ignorée
        Assert.Equal((50m, false, (long?)V1), RegleReglementCredit.Surplus(50, [V0], [(V0, 0m), (V1, 120m)], 0));
    }

    [Theory]
    [InlineData(0, 220)]
    [InlineData(50, 170)]
    [InlineData(150, 70)]
    [InlineData(30, 250)]
    public void Historique_sans_imputation_identique_a_l_ancien_calcul(decimal ouverture, decimal dette)
    {
        // Ancien v_vente_credit (038) : réglé = clamp(réglé total − (ouverture + cumul − montant), 0, montant)
        decimal[] montants = [100, 120];
        var regleTotal = Math.Max(ouverture + montants.Sum() - dette, 0);
        var r = RegleImputationCredit.Calculer(ouverture, dette, [new(V0, 100, 0), new(V1, 120, 0)]);
        var cumul = 0m;
        for (var i = 0; i < montants.Length; i++)
        {
            cumul += montants[i];
            var ancien = Math.Min(Math.Max(regleTotal - (ouverture + cumul - montants[i]), 0), montants[i]);
            Assert.Equal(ancien, r[i].Regle);
        }
    }

    [Fact]
    public void Deux_avoirs_simultanes_le_second_refuse_au_dela_de_la_facture()
    {
        // F de 120 : un premier avoir de 80 est émis ; le second (80), revérifié sous verrou, dépasse ce qui reste (40)
        RegleFacture.VerifierAvoir("facture", "emise", 120, 0, 80, "retour");
        var ex = Assert.Throws<RegleMetierException>(() => RegleFacture.VerifierAvoir("facture", "emise", 120, 80, 80, "retour"));
        Assert.Contains("40", ex.Message);
    }
}
