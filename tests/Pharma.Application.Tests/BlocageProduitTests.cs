using Pharma.Application.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class BlocagesEnMemoire : IBlocageProduits
{
    public BlocageProduit Produit { get; set; } = new(1, "Produit A", false, false, null);
    public Task<BlocageProduit?> LireAsync(long produitId, CancellationToken ct = default) => Task.FromResult<BlocageProduit?>(produitId == Produit.ProduitId ? Produit : null);
    public Task<IReadOnlyList<BlocageProduit>> BloquesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<BlocageProduit>>(Produit.BloqueAchat || Produit.BloqueVente ? [Produit] : []);
    public Task<bool> ModifierAsync(long produitId, bool achat, bool vente, string? motif, CancellationToken ct = default)
    {
        Produit = Produit with { BloqueAchat = achat, BloqueVente = vente, Motif = motif };
        return Task.FromResult(produitId == Produit.ProduitId);
    }
}

public class BlocageProduitTests
{
    [Fact]
    public async Task Blocage_avec_motif_journalise_puis_deblocage_efface_le_motif()
    {
        var b = new BlocagesEnMemoire();
        var journal = new JournalEspion();
        var cas = new ModifierBlocageProduit(b, new DroitsFactices("produits.bloquer"), journal);
        await cas.ExecuterAsync(1, true, false, " litige ", 3);
        Assert.Equal((true, false, "litige"), (b.Produit.BloqueAchat, b.Produit.BloqueVente, b.Produit.Motif));
        await cas.ExecuterAsync(1, false, false, "ancien motif", 3);
        Assert.Null(b.Produit.Motif);
        Assert.Equal(["produit_bloque", "produit_debloque"], journal.Actions);
    }

    [Fact]
    public async Task Motif_obligatoire_et_droit_exige()
    {
        var b = new BlocagesEnMemoire();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierBlocageProduit(b, new DroitsFactices("produits.bloquer"), new JournalEspion()).ExecuterAsync(1, false, true, " ", 3));
        Assert.Equal("motif_blocage", ex.Champ);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ModifierBlocageProduit(b, new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync(1, true, true, "x", 3));
        Assert.False(b.Produit.BloqueVente);
    }
}

public class AutorisationBlocageTests
{
    private static readonly string[] Produits = ["Produit A (litige)"];

    [Fact]
    public async Task Rien_a_valider_sans_produit_bloque() =>
        Assert.Null(await AutorisationBlocage.ExigerAsync(new DroitsFactices(), null, 3, null, null, "à la vente", []));

    [Fact]
    public async Task Employe_habilite_confirme_lui_meme()
    {
        var r = await AutorisationBlocage.ExigerAsync(new DroitsFactices("produits.bloquer"), null, 3, new ValidationBlocage(true), null, "à la vente", Produits);
        Assert.Equal(3, r!.Value.Id);
    }

    [Fact]
    public async Task Sans_accord_ou_sans_code_d_un_habilite_refuse()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            AutorisationBlocage.ExigerAsync(new DroitsFactices("produits.bloquer"), null, 3, null, null, "à la vente", Produits));
        Assert.Equal("produit_bloque", ex.Champ);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            AutorisationBlocage.ExigerAsync(new DroitsFactices("caisse.vendre"), null, 3, new ValidationBlocage(true), null, "à la vente", Produits));
    }
}
