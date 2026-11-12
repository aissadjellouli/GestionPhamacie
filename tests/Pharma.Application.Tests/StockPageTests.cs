using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

internal sealed class StockLectureEnMemoire(IReadOnlyList<LigneStock> lignes) : IStockLecture
{
    public Task<IndicateursStock> IndicateursAsync(CancellationToken ct = default) => Task.FromResult(new IndicateursStock(lignes.Count, 1000, 0));
    public Task<IReadOnlyList<LigneStock>> LignesAsync(CancellationToken ct = default) => Task.FromResult(lignes);
}

internal sealed class EmplacementsDuStock(IReadOnlyList<Emplacement> liste) : IEmplacements
{
    public Task<IReadOnlyList<Emplacement>> ListerAsync(CancellationToken ct = default) => Task.FromResult(liste);
    public Task<bool> CodeExisteAsync(string code, long? saufId, CancellationToken ct = default) => Task.FromResult(false);
    public Task<long> CreerAsync(string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, CancellationToken ct = default) => Task.FromResult(1L);
    public Task<bool> ModifierAsync(long id, string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, bool actif, CancellationToken ct = default) => Task.FromResult(true);
    public Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default) => Task.FromResult<string?>(null);
    public Task RangerAsync(long produitId, long? emplacementId, long? reserveId, CancellationToken ct = default) => Task.CompletedTask;
}

public class StockPageTests
{
    private static LigneStock Ligne(long id, string nom, string? dci, string statut = "ok", ClasseReglementaire classe = ClasseReglementaire.Libre,
        long? emplacement = null, string? code = null, params string[] lots) =>
        new(id, nom, dci, $"613{id:D10}", classe, 10, 30, null, lots.Length, ["Grossiste A"], lots, statut, false, emplacement, code);

    private static ConsulterStock Cas() => new(new StockLectureEnMemoire(
        [
            Ligne(1, "Paracétamol 500 mg cp", "Paracétamol", lots: "B1182"),
            Ligne(2, "Paracétamol 1 g cp", "Paracétamol", "rupture", emplacement: 10, code: "B-01-1"),
            Ligne(3, "Alprazolam 0,25 mg cp", "Alprazolam", classe: ClasseReglementaire.Psychotrope, emplacement: 11, code: "P-01"),
            Ligne(4, "Amoxicilline 1 g cp", "Amoxicilline", "critique"),
            Ligne(5, "Crème mains 50 ml", null)
        ]),
        new EmplacementsDuStock(
        [
            new(10, "B-01-1", "B", "01", "1", null, ZoneEmplacement.Vente, true, 1),
            new(11, "P-01", "P", "01", null, null, ZoneEmplacement.Vente, true, 1)
        ]));

    [Fact]
    public async Task Recherche_sans_majuscules_ni_accents_et_pages_du_serveur()
    {
        var p = await Cas().PageAsync(new FiltreStock("paracetamol"), new DemandePage(1, 5));
        Assert.Equal(2, p.Page.Total);
        var page2 = await Cas().PageAsync(new FiltreStock(), new DemandePage(2, 2));
        Assert.Equal((5, 2), (page2.Page.Total, page2.Page.Lignes.Count));
        Assert.Equal(5, page2.Indicateurs.ReferencesEnStock);                       // indicateurs de tout le stock
        Assert.Single((await Cas().PageAsync(new FiltreStock("b1182"), new DemandePage())).Page.Lignes);   // numéro de lot
    }

    [Fact]
    public async Task Filtres_commander_psychotropes_et_rayon()
    {
        Assert.Equal(2, (await Cas().PageAsync(new FiltreStock(Filtre: "commander"), new DemandePage())).Page.Total);
        Assert.Equal("Alprazolam 0,25 mg cp", Assert.Single((await Cas().PageAsync(new FiltreStock(Filtre: "psychotropes"), new DemandePage())).Page.Lignes).NomCommercial);
        Assert.Equal(2L, Assert.Single((await Cas().PageAsync(new FiltreStock(Rayon: "B"), new DemandePage())).Page.Lignes).ProduitId);
        Assert.Equal(3, (await Cas().PageAsync(new FiltreStock(Rayon: "-"), new DemandePage())).Page.Total);   // non rangés
    }

    [Fact]
    public async Task Propositions_pour_aider_la_saisie()
    {
        Assert.Empty((await Cas().PageAsync(new FiltreStock("p"), new DemandePage())).Suggestions);            // moins de 2 caractères
        var s = (await Cas().PageAsync(new FiltreStock("para"), new DemandePage())).Suggestions;
        Assert.Equal("Paracétamol", s[0]);                                                                     // commence par le texte, d'abord
        Assert.Contains("Paracétamol 500 mg cp", s);
        Assert.True(s.Count <= 8);
        Assert.Contains("P-01", (await Cas().PageAsync(new FiltreStock("p-0"), new DemandePage())).Suggestions);   // emplacement
    }
}
