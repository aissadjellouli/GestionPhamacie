using Pharma.Domain.Achats;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Parapharmacie distinguée dans le catalogue (décision du titulaire du 2026-11-20).</summary>
public class ParapharmacieTests
{
    private static FicheProduit Creme(string? dci = null, string? dosage = null, string? forme = null) =>
        new(" Crème mains 50 ml ", dci!, dosage!, forme!, 450m, ClasseReglementaire.Libre, null, null, false, 19m, null, Categorie: "hygiene");

    [Fact]
    public void Parapharmacie_DCI_dosage_et_forme_facultatifs()
    {
        var f = RegleProduit.Normaliser(Creme(), categorieMedicament: false);
        Assert.Equal(("Crème mains 50 ml", "", "", ""), (f.NomCommercial, f.Dci, f.Dosage, f.Forme));
        Assert.Equal("Urée", RegleProduit.Normaliser(Creme(dci: " Urée "), categorieMedicament: false).Dci);
    }

    [Fact]
    public void Medicament_DCI_dosage_et_forme_toujours_obligatoires()
    {
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Creme()));
        Assert.Throws<RegleMetierException>(() => RegleProduit.Normaliser(Creme(dci: "Urée", dosage: "10 %")));   // forme manquante
    }

    [Fact]
    public void Categorie_medicament_jamais_controlee()
    {
        RegleProduit.ControlerCategorie(true, "stupefiant", true);
        RegleProduit.ControlerCategorie(false, "libre", false);
    }

    [Fact]
    public void Parapharmacie_refusee_dans_la_nomenclature_avec_un_message_clair()
    {
        var e = Assert.Throws<RegleMetierException>(() => RegleProduit.ControlerCategorie(false, "libre", true));
        Assert.Contains("nomenclature nationale", e.Message);
        Assert.Equal("categorie", e.Champ);
    }

    [Theory]
    [InlineData("liste_i")]
    [InlineData("liste_ii")]
    [InlineData("psychotrope")]
    [InlineData("stupefiant")]
    [InlineData(null)]
    public void Parapharmacie_refusee_hors_classe_libre(string? classe)
    {
        var e = Assert.Throws<RegleMetierException>(() => RegleProduit.ControlerCategorie(false, classe, false));
        Assert.Contains("hors liste", e.Message);
        Assert.Equal("categorie", e.Champ);
    }

    [Theory]
    [InlineData("libre", "hors liste")]
    [InlineData(null, "hors liste")]
    [InlineData("liste_i", "liste I")]
    [InlineData("liste_ii", "liste II")]
    [InlineData("stupefiant", "stupéfiant (liste I)")]
    public void Controle_du_SHP_classe_libre_ecrite_hors_liste_et_non_parapharmacie(string? classe, string attendu) =>
        Assert.Equal(attendu, RegleShp.LibelleClasse(classe));

    [Fact]
    public void Reprise_la_colonne_categorie_n_est_plus_prise_pour_la_classe()
    {
        var classe = DetectionReprise.Champs[CibleReprise.Produits].First(c => c.Code == "classe");
        Assert.DoesNotContain("categorie", classe.MotsCles);
        Assert.True(DetectionReprise.Score(classe, new ColonneSource("CATEGORIE", "VARCHAR", ["Médicament", "Parapharmacie"])) < 0.4);
        Assert.True(DetectionReprise.Score(classe, new ColonneSource("CLASSE", "VARCHAR", ["Liste I", "Libre"])) >= 0.4);
    }

    [Fact]
    public void Produit_passe_en_parapharmacie_une_remise_a_la_caisse_et_non_un_don()
    {
        var jour = new DateOnly(2026, 11, 20);
        var rattrape = new Produit { Id = 31, NomCommercial = "Crème rattrapée", PrixPublic = 1000m, Categorie = "parapharmacie", EstMedicament = false };
        var lot = new Lot { Id = 310, ProduitId = 31, NumeroLot = "C1", QuantiteDisponible = 5, DatePeremption = jour.AddDays(300) };
        var v = new Vente();
        v.AjouterProduit(rattrape, lot, 1, jour);
        v.AppliquerRemise(10);
        Assert.Equal((100m, 0m, 900m), (v.MontantRemise, v.MontantDon, v.PartPatient));
    }
}
