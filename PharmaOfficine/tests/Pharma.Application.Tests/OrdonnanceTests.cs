using Pharma.Application.Caisse;
using Pharma.Application.Catalogue;
using Pharma.Application.Ordonnances;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class ScansEnMemoire : IStockageOrdonnances
{
    public HashSet<string> Jetons { get; } = ["0123456789abcdef0123456789abcdef.jpg"];
    public Task<string> EnregistrerAsync(byte[] contenu, string extension, CancellationToken ct = default)
    {
        var jeton = $"{Guid.NewGuid():N}.{extension}";
        Jetons.Add(jeton);
        return Task.FromResult(jeton);
    }
    public bool Existe(string jeton) => Jetons.Contains(jeton);
}

/// <summary>Catalogue factice : un texte contenant « amox » ou « parac » trouve le produit correspondant.</summary>
internal sealed class RapprochementFactice : IRapprochementCatalogue
{
    public List<string> Recus { get; } = [];

    public Task<IReadOnlyList<ProduitCandidat>> RapprocherAsync(string texte, int limite, double scoreMinimal, CancellationToken ct = default)
    {
        Recus.Add(texte);
        IReadOnlyList<ProduitCandidat> r = texte.ToLowerInvariant() switch
        {
            var t when t.Contains("amox") => [new ProduitCandidat(2, "Amoxicilline 1 g", "Amoxicilline", "1 g", "comprimé", 350, 20, ClasseReglementaire.ListeI, 0.9)],
            var t when t.Contains("parac") => [new ProduitCandidat(1, "Paracétamol 500 mg", "Paracétamol", "500 mg", "comprimé", 120, 300, ClasseReglementaire.Libre, 0.8)],
            _ => []
        };
        return Task.FromResult(r);
    }
}

internal sealed class AlternativesEnMemoire(params ProduitAlternatif[] liste) : IAlternativesLecture
{
    public Task<IReadOnlyList<ProduitAlternatif>> AlternativesAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitAlternatif>>(liste);
}

public class OrdonnanceTests
{
    [Fact]
    public async Task Lignes_rapprochees_bruit_ignore_et_doublons_retires()
    {
        var catalogue = new RapprochementFactice();
        var r = await new AnalyserOrdonnance(catalogue).ExecuterAsync(
        [
            "Dr Médecin Fictif",            // aucune correspondance
            "08/11/2026",                   // trop peu de lettres : pas envoyé au catalogue
            "  AMOXICILLINE   1g cp  ",      // espaces nettoyés
            "1 cp matin et soir 7 jours",
            "Amoxicilline (suite)",         // même produit : pas proposé deux fois
            "Paracetamol 500"
        ]);

        Assert.Equal(["AMOXICILLINE 1g cp", "Paracetamol 500"], r.Lignes.Select(l => l.Texte));
        Assert.Equal(3, r.LignesIgnorees);
        Assert.DoesNotContain("08/11/2026", catalogue.Recus);
    }

    [Theory]
    [InlineData("1) Amoxicilline 1 g : 1 cp matin et soir. 2 boites", 2)]
    [InlineData("Amoxicilline 1g - 3 boîtes", 3)]
    [InlineData("Amoxicilline 1 g QSP 1 bte", 1)]
    [InlineData("Amoxicilline 1 g : 1 cp matin et soir 7 jours", null)]
    [InlineData("Amoxicilline 1 g : 50 boites", null)]   // invraisemblable : au caissier de saisir
    public async Task Nombre_de_boites_lu_quand_il_est_ecrit(string ligne, int? attendu)
    {
        var r = await new AnalyserOrdonnance(new RapprochementFactice()).ExecuterAsync([ligne]);
        Assert.Equal(attendu, Assert.Single(r.Lignes).NombreBoites);
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1 }, true)]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1 }, true)]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 1 }, false)]   // PDF brut : converti en image par le poste avant l'envoi
    [InlineData(new byte[] { }, false)]
    public async Task Seuls_jpeg_et_png_sont_acceptes(byte[] contenu, bool accepte)
    {
        var cas = new EnregistrerScanOrdonnance(new ScansEnMemoire());
        if (accepte) Assert.Matches("^[0-9a-f]{32}\\.(jpg|png)$", await cas.ExecuterAsync(contenu));
        else await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(contenu));
    }

    [Fact]
    public async Task Equivalents_exacts_d_abord()
    {
        var r = await new ListerAlternatives(new AlternativesEnMemoire(
            new ProduitAlternatif(5, "Autre dosage", 100, 50, Exact: false),
            new ProduitAlternatif(4, "Exact peu de stock", 100, 3, Exact: true),
            new ProduitAlternatif(3, "Exact beaucoup de stock", 100, 30, Exact: true))).ExecuterAsync(1);
        Assert.Equal([3L, 4L], r.Select(a => a.ProduitId));
    }

    [Fact]
    public async Task Sans_equivalent_exact_meme_dci_autre_dosage()
    {
        var r = await new ListerAlternatives(new AlternativesEnMemoire(
            new ProduitAlternatif(5, "Amoxicilline 500 mg", 200, 40, Exact: false, Dosage: "500 mg"))).ExecuterAsync(2);
        Assert.False(Assert.Single(r).Exact);
    }

    private static Encaisser Encaisser(ScansEnMemoire scans) => new(
        new CatalogueEnMemoire(), new PatientsEnMemoire(), new VentesEspion(), new SessionsEnMemoire(),
        new DroitsFactices("caisse.vendre", "psychotropes.delivrer"), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), scans);

    private static CommandeVente Commande(long produit, long lot, OrdonnanceSaisie o) =>
        new([new LigneCommande(produit, lot, 1)], false, null, null, PatientId: 3, o, 0, ModePaiement.Especes, null);

    [Fact]
    public async Task Psychotrope_sans_medecin_refuse()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Encaisser(new ScansEnMemoire())
            .ExecuterAsync(Commande(7, 70, new OrdonnanceSaisie(null, CatalogueEnMemoire.Aujourdhui, null)), 5));
        Assert.Contains("médecin", ex.Message);
    }

    [Fact]
    public async Task Ordonnance_scannee_sans_medecin_acceptee_hors_psychotrope() =>
        await Encaisser(new ScansEnMemoire()).ExecuterAsync(
            Commande(2, 20, new OrdonnanceSaisie(null, CatalogueEnMemoire.Aujourdhui, null, "0123456789abcdef0123456789abcdef.jpg", "Amoxicilline 1g")), 5);

    [Fact]
    public async Task Image_inconnue_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => Encaisser(new ScansEnMemoire()).ExecuterAsync(
            Commande(2, 20, new OrdonnanceSaisie(null, CatalogueEnMemoire.Aujourdhui, null, "../../windows/win.ini", null)), 5));

    [Fact]
    public async Task Prescription_datee_du_futur_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => Encaisser(new ScansEnMemoire()).ExecuterAsync(
            Commande(2, 20, new OrdonnanceSaisie(null, CatalogueEnMemoire.Aujourdhui.AddDays(1), null)), 5));
}
