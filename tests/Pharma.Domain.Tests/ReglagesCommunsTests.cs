using Pharma.Domain.Reglages;

namespace Pharma.Domain.Tests;

/// <summary>Lot S7.4 : liste fermée des réglages communs du titulaire, lecture stricte et bornes (mêmes que la base centrale, script 035).</summary>
public class ReglagesCommunsTests
{
    [Fact]
    public void Liste_fermee_de_seuils_d_alerte_seulement()
    {
        Assert.Equal(["jours_orange", "jours_rouge", "horizon_risque_jours", "delai_fournisseur_jours", "jours_securite", "jours_cible_commande",
            "jours_dormant", "seuil_lot_delaisse", "seuil_lot_delaisse_collectif"], ReglagesCommuns.Liste.Select(r => r.Cle));
        Assert.All(ReglagesCommuns.Liste, r => Assert.True(r.Minimum >= 0 && r.Maximum > r.Minimum));
    }

    [Theory]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60}}""", null)]
    [InlineData("""{"format":"reglages-v1","reglages":{"taux_tva":19}}""", "hors de la liste permise")]
    [InlineData("""{"format":"reglages-v1","reglages":{"prix_public":100}}""", "hors de la liste permise")]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":0}}""", "de 1 à 1095")]
    [InlineData("""{"format":"reglages-v1","reglages":{"delai_fournisseur_jours":91}}""", "de 0 à 90")]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":12.5}}""", "nombre entier")]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":"120"}}""", "nombre entier")]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":60,"jours_rouge":60}}""", "plus courte")]
    [InlineData("""{"format":"reglages-v1","reglages":{}}""", "au moins un")]
    [InlineData("""{"format":"autre","reglages":{"jours_orange":120}}""", "format non pris en charge")]
    [InlineData("""{"format":"reglages-v1","reglages":{"jours_orange":120},"en_plus":1}""", "format non pris en charge")]
    [InlineData("pas du json", "illisibles")]
    [InlineData(null, "absentes")]
    public void Lecture_stricte(string? json, string? erreur)
    {
        var (valeurs, erreurs) = ReglagesCommuns.Lire(json);
        if (erreur is null)
        {
            Assert.Empty(erreurs);
            Assert.Equal(120, valeurs["jours_orange"]);
        }
        else Assert.Contains(erreur, Assert.Single(erreurs));
    }

    [Fact]
    public void Json_puis_lecture_redonnent_les_memes_valeurs()
    {
        var v = new Dictionary<string, int> { ["jours_rouge"] = 60, ["jours_orange"] = 120 };
        var json = ReglagesCommuns.Json(v);
        Assert.Equal("""{"format":"reglages-v1","reglages":{"jours_orange":120,"jours_rouge":60}}""", json);
        Assert.Equal(v.OrderBy(x => x.Key), ReglagesCommuns.Lire(json).Valeurs.OrderBy(x => x.Key));
    }
}
