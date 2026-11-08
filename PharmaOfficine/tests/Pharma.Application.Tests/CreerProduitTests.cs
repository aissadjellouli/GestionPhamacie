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
    public Task<long> CreerAsync(FicheProduit f, CancellationToken ct = default) { Crees.Add(f); return Task.FromResult(50L); }
    public Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
}

public class CreerProduitTests
{
    private static FicheProduit Fiche(string nom = "Sintrom 4 mg cp", string dosage = "4 mg", string? code = null) =>
        new(nom, "Acénocoumarol", dosage, "comprimé", 280m, ClasseReglementaire.ListeI, code, 280m, true, 0m, 5);

    [Fact]
    public async Task Creation_journalisee_avec_le_droit()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var id = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync(Fiche(), 2);
        Assert.Equal(50, id);
        Assert.Equal("Sintrom 4 mg cp", catalogue.Crees.Single().NomCommercial);
        Assert.Equal(["produit_cree"], journal.Actions);
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
