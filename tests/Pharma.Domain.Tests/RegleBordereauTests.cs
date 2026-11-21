using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Bordereaux Chifa / MDN (2026-11-15, script 110).</summary>
public class RegleBordereauTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);

    [Theory]
    [InlineData("depose", 1000, 0, 0, 0, "depose")]
    [InlineData("depose", 1000, 400, 0, 0, "paye_partiel")]
    [InlineData("depose", 1000, 0, 300, 0, "depose")]            // rejet seul, reste dû
    [InlineData("paye_partiel", 1000, 900, 100, 1, "paye")]      // plus rien à recevoir, rejet à traiter
    [InlineData("paye", 1000, 900, 100, 0, "solde")]             // rejet traité
    [InlineData("depose", 1000, 1000, 0, 0, "solde")]
    [InlineData("preparation", 1000, 0, 0, 0, "preparation")]
    public void Statut_apres_versement_ou_rejet(string actuel, decimal attendu, decimal verse, decimal rejete, int aTraiter, string attenduStatut) =>
        Assert.Equal(attenduStatut, RegleBordereau.StatutApres(actuel, attendu, verse, rejete, aTraiter));

    [Fact]
    public void Numero_saisi_et_entete()
    {
        Assert.Equal("100126", RegleBordereau.Numero(" 100126 "));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.Numero(""));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.Numero(new string('1', 31)));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.Numero("12;DROP"));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierEntete(new(2026, 10, 31), new(2026, 10, 1), null, null, Aujourdhui));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierEntete(new(2026, 10, 1), new(2026, 10, 31), null, 10.001m, Aujourdhui));
        Assert.Equal(-28.66m, RegleBordereau.Ecart(928.66m, 900m));
        Assert.Null(RegleBordereau.Ecart(928.66m, null));
    }

    [Fact]
    public void Rattachement_une_vente_un_bordereau_meme_organisme_et_centre()
    {
        void Ok(string canal = "chifa", long org = 1, long? centre = 3, bool exclue = false, bool sur = false, string? facture = "06931126",
            bool definitif = false, decimal part = 800, string statut = "validee") =>
            RegleBordereau.VerifierRattachement("chifa", 1, 3, canal, statut, org, centre, exclue, sur, facture, definitif, part);

        Ok();
        Ok(centre: null);
        Assert.Throws<RegleMetierException>(() => Ok(sur: true));
        Assert.Throws<RegleMetierException>(() => Ok(canal: "mdn"));
        Assert.Throws<RegleMetierException>(() => Ok(org: 2));
        Assert.Throws<RegleMetierException>(() => Ok(centre: 5));
        Assert.Throws<RegleMetierException>(() => Ok(exclue: true));
        Assert.Throws<RegleMetierException>(() => Ok(facture: null));
        Assert.Throws<RegleMetierException>(() => Ok(definitif: true));
        Assert.Throws<RegleMetierException>(() => Ok(part: 0));
        Assert.Throws<RegleMetierException>(() => Ok(statut: "annulee"));
        // MDN : pas de n° de facture Chifa exigé, centre facultatif
        RegleBordereau.VerifierRattachement("mdn", 9, null, "mdn", "validee", 9, null, false, false, null, false, 500);
    }

    [Fact]
    public void Rejet_et_suites()
    {
        RegleBordereau.VerifierRejet("depose", false, 800, 800);
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierRejet("preparation", false, 100, 800));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierRejet("solde", false, 100, 800));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierRejet("depose", true, 100, 800));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierRejet("depose", false, 800.01m, 800));
        Assert.Equal("perte", RegleBordereau.VerifierSuite(" Perte ", true, null, false));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierSuite("patient", true, null, false));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierSuite("perte", true, "redeposer", true));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierSuite("perte", false, null, true));
    }

    [Fact]
    public void Imputation_d_un_versement_partiel_ou_entier()
    {
        RegleBordereau.VerifierImputations(1500, 1, [(1, "B1", 1, "depose", 1000, 1000), (2, "B2", 1, "paye_partiel", 800, 400)]);
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(500, 1, [(1, "B1", 1, "depose", 1000, 600)]));        // > versement
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(5000, 1, [(1, "B1", 1, "depose", 1000, 1000.01m)])); // > reste
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(5000, 1, [(1, "B1", 2, "depose", 1000, 100)]));      // autre organisme
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(5000, 1, [(1, "B1", 1, "solde", 1000, 100)]));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(5000, 1, [(1, "B1", 1, "preparation", 1000, 100)]));
        Assert.Throws<RegleMetierException>(() => RegleBordereau.VerifierImputations(5000, 1, [(1, "B1", 1, "depose", 1000, 100), (1, "B1", 1, "depose", 1000, 100)]));
    }

    [Fact]
    public void Vente_sur_un_bordereau_depose_non_annulable_et_ventes_a_deposer_en_retard()
    {
        // En préparation : la retirer d'abord du bordereau ; déposée : message du titulaire (2026-11-21, script 155)
        var e = Assert.Throws<RegleMetierException>(() => RegleAnnulation.VerifierAnnulable("validee", true));
        Assert.Contains("retirez-la d’abord du bordereau", e.Message);
        var d = Assert.Throws<RegleMetierException>(() => RegleAnnulation.VerifierAnnulable("validee", true, null, deposee: true));
        Assert.StartsWith("Vente déposée à la CNAS : retour impossible dans le logiciel", d.Message);
        Assert.False(RegleBordereau.EnRetard(new(2026, 11, 8), RegleBordereau.DelaiDepotParDefaut, Aujourdhui));   // 7 jours : dans le délai
        Assert.True(RegleBordereau.EnRetard(new(2026, 11, 7), RegleBordereau.DelaiDepotParDefaut, Aujourdhui));
        Assert.True(RegleBordereau.EnRetard(new(2026, 9, 30), RegleBordereau.DelaiPaiementParDefaut, Aujourdhui)); // 46 jours après le dépôt
    }
}
