using Pharma.Application.Achats;
using Pharma.Application.Catalogue;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class SortiesEnMemoire : ISortiesStock
{
    public LotEnStock Lot { get; set; } = new(9, 1, "Vitamine D3", "VD3", new DateOnly(2026, 1, 1), 10, false, false);
    public SortieStockAEcrire? Ecrite { get; private set; }
    public Task<IReadOnlyList<LotEnStock>> LotsAsync(long produitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LotEnStock>>([Lot]);
    public Task<LotEnStock?> LotAsync(long lotId, CancellationToken ct = default) => Task.FromResult<LotEnStock?>(lotId == Lot.LotId ? Lot : null);
    public Task<int> SortirAsync(SortieStockAEcrire s, CancellationToken ct = default) { Ecrite = s; return Task.FromResult(s.Quantite); }
}

internal sealed class GrossistesEnMemoire : IGrossistes
{
    public FicheGrossiste? Enregistre { get; private set; }
    public bool Existe { get; set; }
    public Task<long> EnregistrerAsync(FicheGrossiste f, CancellationToken ct = default) { Enregistre = f; return Task.FromResult(f.Id ?? 42L); }
    public Task<bool> RaisonSocialeExisteAsync(string raisonSociale, long? sauf, CancellationToken ct = default) => Task.FromResult(Existe);
}

internal sealed class ProduitsModifEnMemoire : IProduitsModification
{
    public FicheProduitModifiable Fiche { get; set; } = new(5, "Produit", "DCI", "10 mg", "cp", null, 100m, 90m, true, 0m, true, "liste_i");
    public FicheProduitModifiable? Modifiee { get; private set; }
    public Task<FicheProduitModifiable?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<FicheProduitModifiable?>(id == Fiche.Id ? Fiche : null);
    public Task<bool> CodeBarreAutreProduitAsync(string codeBarre, long id, CancellationToken ct = default) => Task.FromResult(false);
    public bool ClasseImposee { get; private set; }
    public Task ModifierAsync(FicheProduitModifiable f, bool classeImposee, long utilisateurId, CancellationToken ct = default)
    {
        (Modifiee, ClasseImposee) = (f, classeImposee);
        return Task.CompletedTask;
    }
    /// <summary>Substance surveillée : alprazolam (psychotrope).</summary>
    public Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default) =>
        Task.FromResult<string?>(dci.Contains("alprazolam", StringComparison.OrdinalIgnoreCase) ? "psychotrope" : null);
}

public class RecetteCorrectionsTests
{
    [Fact]
    public async Task Sortie_de_stock_perime_ou_casse_bornee_et_journalisee()
    {
        var s = new SortiesEnMemoire();
        var journal = new JournalEspion();
        var cas = new DeclarerSortieStock(s, new DroitsFactices("stock.declarer_perime"), journal);
        Assert.Equal(3, await cas.ExecuterAsync(9, 3, "perime", null, 1));
        Assert.Equal((3, "perime", "Périmé"), (s.Ecrite!.Quantite, s.Ecrite.Motif, s.Ecrite.Reference));
        Assert.Equal(["sortie_stock"], journal.Actions);
        Assert.Equal("quantite_sortie", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(9, 11, "perime", null, 1))).Champ);
        Assert.Equal("commentaire_sortie", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(9, 1, "casse", " ", 1))).Champ);
        Assert.Equal("motif_sortie", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(9, 1, "vol", null, 1))).Champ);
        await Assert.ThrowsAnyAsync<Exception>(() => new DeclarerSortieStock(s, new DroitsFactices("caisse.vendre"), journal).ExecuterAsync(9, 1, "perime", null, 1));
    }

    [Fact]
    public async Task Grossiste_cree_avec_controles_et_doublon_refuse()
    {
        var g = new GrossistesEnMemoire();
        var cas = new EnregistrerGrossiste(g, new DroitsFactices("achats.commander"), new JournalEspion());
        Assert.Equal(42, await cas.ExecuterAsync(new FicheGrossiste(null, " Grossiste Recette ", "0550 11 22 33", null, null, null, null, 30, true), 1));
        Assert.Equal("Grossiste Recette", g.Enregistre!.RaisonSociale);
        Assert.Equal("raison_sociale", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(new FicheGrossiste(null, " ", null, null, null, null, null, null, true), 1))).Champ);
        Assert.Equal("delai_reprise", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(new FicheGrossiste(null, "X", null, null, null, null, null, 0, true), 1))).Champ);
        g.Existe = true;
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(new FicheGrossiste(null, "X", null, null, null, null, null, null, true), 1));
    }

    [Fact]
    public async Task Modifier_un_produit_le_prix_exige_son_droit_et_le_journal_garde_avant_apres()
    {
        var p = new ProduitsModifEnMemoire();
        var journal = new JournalEspion();
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync(p.Fiche with { NomCommercial = "Produit modifié", Actif = false }, 1);
        Assert.Equal(("Produit modifié", false, "liste_i"), (p.Modifiee!.NomCommercial, p.Modifiee.Actif, p.Modifiee.Classe));
        Assert.Equal(["produit_modifie"], journal.Actions);
        await Assert.ThrowsAnyAsync<Exception>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { PrixPublic = 120m }, 1));
        var j2 = new JournalEspion();
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer", "prix.modifier"), j2).ExecuterAsync(p.Fiche with { PrixPublic = 120m }, 1);
        Assert.Contains("prix_modifie", j2.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { Remboursable = true, TarifReference = null }, 1));
    }

    [Fact]
    public async Task Nouvelle_dci_de_psychotrope_donnee_par_un_employe_classe_d_office_et_a_verifier()
    {
        var p = new ProduitsModifEnMemoire();
        var journal = new JournalEspion();
        var information = await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync(p.Fiche with { Dci = "Alprazolam" }, 3);
        Assert.Equal(("psychotrope", true), (p.Modifiee!.Classe, p.ClasseImposee));
        Assert.Contains("invendable", information);
        Assert.Equal(true, journal.Details.Single()["a_verifier"]);
    }

    [Fact]
    public async Task Titulaire_ou_dci_inchangee_classe_non_imposee()
    {
        var p = new ProduitsModifEnMemoire();
        Assert.Null(await new ModifierProduit(p, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(p.Fiche with { Dci = "Alprazolam" }, 1));
        Assert.Equal(("liste_i", false), (p.Modifiee!.Classe, p.ClasseImposee));

        // DCI déjà surveillée et inchangée (signalée au titulaire par « Produits à vérifier ») : une autre modification ne la reclasse pas
        p.Fiche = p.Fiche with { Dci = "Alprazolam" };
        Assert.Null(await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(p.Fiche with { NomCommercial = "Autre nom" }, 3));
        Assert.Equal(("liste_i", false), (p.Modifiee!.Classe, p.ClasseImposee));
    }
}
