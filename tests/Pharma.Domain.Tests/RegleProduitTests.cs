using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class RegleProduitTests
{
    private static FicheProduit Fiche(string? code = null, decimal prix = 280m, bool remboursable = false, decimal? tarif = null) =>
        new("  Sintrom 4 mg cp ", " Acénocoumarol ", "4 mg", "comprimé", prix, ClasseReglementaire.ListeI, code, tarif, remboursable, 0m, 0);

    [Fact]
    public void Fiche_valide_normalisee()
    {
        var f = RegleProduit.Normaliser(Fiche());
        Assert.Equal("Sintrom 4 mg cp", f.NomCommercial);
        Assert.Equal("Acénocoumarol", f.Dci);
    }

    [Fact]
    public void Champs_obligatoires()
    {
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Fiche() with { Dci = " " }));
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Fiche() with { Forme = "" }));
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Fiche(prix: 0)));
    }

    [Fact]
    public void Remboursable_exige_un_tarif_de_reference() =>
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Fiche(remboursable: true)));

    [Theory]
    [InlineData("6130000000107", "6130000000107")]
    [InlineData("06130000000107", "6130000000107")]   // GTIN-14 du Data Matrix
    public void Code_barres_ean13_accepte(string saisi, string attendu) =>
        Assert.Equal(attendu, RegleProduit.Normaliser(Fiche(saisi)).CodeBarre);

    [Theory]
    [InlineData("6130000000108")]   // clé fausse
    [InlineData("2000000000077")]   // réservé aux étiquettes internes
    [InlineData("123")]
    public void Code_barres_refuse(string saisi) =>
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Fiche(saisi)));
}
