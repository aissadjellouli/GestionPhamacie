using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class CatalogueEcritureEnMemoire : ICatalogueEcriture
{
    public List<FicheProduit> Crees { get; } = [];
    public HashSet<string> Codes { get; } = ["6130000000107"];
    public Task<bool> CodeBarreExisteAsync(string code, CancellationToken ct = default) => Task.FromResult(Codes.Contains(code));
    public Task<string?> DoublonAsync(string nom, string dosage, string forme, CancellationToken ct = default) =>
        Task.FromResult<string?>(nom.Equals("paracétamol 500 mg cp", StringComparison.OrdinalIgnoreCase) && dosage == "500 mg" ? "Paracétamol 500 mg cp" : null);
    public List<bool> AVerifier { get; } = [];
    public Task<long> CreerAsync(FicheProduit f, bool aVerifier, CancellationToken ct = default) { Crees.Add(f); AVerifier.Add(aVerifier); return Task.FromResult(50L); }
    public Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>Substances surveillées : alprazolam (psychotrope), morphine (stupéfiant).</summary>
    public Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default) =>
        Task.FromResult<string?>(dci.Contains("alprazolam", StringComparison.OrdinalIgnoreCase) ? "psychotrope"
            : dci.Contains("morphine", StringComparison.OrdinalIgnoreCase) ? "stupefiant" : null);
}

public class CreerProduitTests
{
    private static FicheProduit Fiche(string nom = "Sintrom 4 mg cp", string dosage = "4 mg", string? code = null) =>
        new(nom, "Acénocoumarol", dosage, "comprimé", 280m, ClasseReglementaire.ListeI, code, 280m, true, 0m, 5);

    [Fact]
    public async Task Creation_journalisee_avec_le_droit()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync(Fiche(), 2);
        Assert.Equal((50, null), (cree.Id, cree.Information));
        Assert.Equal("Sintrom 4 mg cp", catalogue.Crees.Single().NomCommercial);
        Assert.Equal(ClasseReglementaire.ListeI, catalogue.Crees.Single().Classe);
        Assert.False(catalogue.AVerifier.Single());
        Assert.Equal(["produit_cree"], journal.Actions);
    }

    private static FicheProduit Psychotrope(ClasseReglementaire classe, string dci = "Alprazolam") =>
        new("Xanax 0,25 mg cp", dci, "0,25 mg", "comprimé", 190m, classe, null, null, false, 0m, null);

    [Theory]
    [InlineData("Alprazolam", ClasseReglementaire.Psychotrope)]
    [InlineData("Sulfate de MORPHINE", ClasseReglementaire.Stupefiant)]
    public async Task Psychotrope_cree_en_classe_libre_par_un_employe_classe_d_office_et_a_verifier(string dci, ClasseReglementaire attendue)
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer"), journal)
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre, dci), 3);
        Assert.Equal(attendue, catalogue.Crees.Single().Classe);
        Assert.True(catalogue.AVerifier.Single());
        Assert.Contains("invendable jusqu’à ce que le titulaire confirme sa classe", cree.Information);
        Assert.Equal(true, journal.Details.Single()["a_verifier"]);
        Assert.Equal("libre", journal.Details.Single()["classe_choisie"]);
    }

    [Fact]
    public async Task Titulaire_ou_classe_deja_sous_registre_ou_dci_non_surveillee_non_concernes()
    {
        var titulaire = new CatalogueEcritureEnMemoire();
        var r1 = await new CreerProduit(titulaire, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre), 1);
        Assert.Equal((ClasseReglementaire.Libre, false, null), (titulaire.Crees.Single().Classe, titulaire.AVerifier.Single(), r1.Information));

        var dejaSousRegistre = new CatalogueEcritureEnMemoire();
        var r2 = await new CreerProduit(dejaSousRegistre, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Psychotrope), 3);
        Assert.Equal((ClasseReglementaire.Psychotrope, false, null), (dejaSousRegistre.Crees.Single().Classe, dejaSousRegistre.AVerifier.Single(), r2.Information));

        var nonSurveillee = new CatalogueEcritureEnMemoire();
        await new CreerProduit(nonSurveillee, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(Fiche(), 3);
        Assert.Equal((ClasseReglementaire.ListeI, false), (nonSurveillee.Crees.Single().Classe, nonSurveillee.AVerifier.Single()));
    }

    [Fact]
    public async Task Sans_le_droit_refus() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CreerProduit(new CatalogueEcritureEnMemoire(), new DroitsFactices("stock.consulter"), new JournalEspion()).ExecuterAsync(Fiche(), 3));

    [Fact]
    public async Task Doublon_et_code_barres_deja_pris_refuses()
    {
        var cas = new CreerProduit(new CatalogueEcritureEnMemoire(), new DroitsFactices("catalogue.creer"), new JournalEspion());
        Assert.Contains("existe déjà", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Fiche("PARACÉTAMOL 500 mg cp", "500 mg"), 2))).Message);
        Assert.Contains("déjà utilisé", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Fiche(code: "6130000000107"), 2))).Message);
    }
}
