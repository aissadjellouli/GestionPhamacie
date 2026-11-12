using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class AccordTests
{
    [Theory]
    [InlineData("3 lot(s)", "3 lots")]
    [InlineData("1 lot(s)", "1 lot")]
    [InlineData("0 lot(s)", "0 lot")]
    [InlineData("1 boîte(s) ajoutée(s) au bon", "1 boîte ajoutée au bon")]
    [InlineData("12 étiquette(s) interne(s) créée(s) pour le lot", "12 étiquettes internes créées pour le lot")]
    [InlineData("44 boîte(s) · 41 100 DA", "44 boîtes · 41 100 DA")]
    [InlineData("1 200 boîte(s)", "1 200 boîtes")]
    [InlineData("1,5 boîte(s)", "1,5 boîte")]
    [InlineData("2,5 boîte(s)", "2,5 boîtes")]
    [InlineData("3 lot(s) en écart, 1 boîte(s) manquante(s)", "3 lots en écart, 1 boîte manquante")]
    [InlineData("Lots (2 lot(s))", "Lots (2 lots)")]
    [InlineData("boîte(s) déjà enregistrée(s)", "boîte(s) déjà enregistrée(s)")]   // sans nombre : laissé tel quel
    [InlineData("1 boîte(s), lot(s) à vérifier", "1 boîte, lot(s) à vérifier")]    // la virgule arrête le nombre
    [InlineData("Facture(s) (s) 3", "Facture(s) (s) 3")]
    public void Accorde_les_s_selon_le_nombre_qui_precede(string texte, string attendu) =>
        Assert.Equal(attendu, Accord.Texte(texte));

    [Fact]
    public void Le_nombre_peut_venir_du_texte_precedent()
    {
        var (debut, n) = Accord.Accorder("Réception : 1", null);
        Assert.Equal(" lot entré", Accord.Accorder(" lot(s) entré(s)", n).Texte);
        Assert.Equal("Réception : 1", debut);
    }

    [Fact]
    public void Texte_vide_ou_null() => Assert.Equal("", Accord.Texte(null));
}
