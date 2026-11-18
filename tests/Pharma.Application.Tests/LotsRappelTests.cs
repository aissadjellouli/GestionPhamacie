using Pharma.Application.Tracabilite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>
/// Numéros de lot proposés au rappel (décision du titulaire, 2026-11-18) : épuisés compris, 10 au plus, produit obligatoire.
/// </summary>
public class LotsRappelTests
{
    private sealed class LotsFactices(int nombre, int epuises = 0) : ILotsRappelables
    {
        public List<(long Produit, string Texte, int Limite)> Appels { get; } = [];
        public Task<IReadOnlyList<LotRappelable>> LotsAsync(long produitId, string texte, int limite, CancellationToken ct = default)
        {
            Appels.Add((produitId, texte, limite));
            return Task.FromResult<IReadOnlyList<LotRappelable>>(Enumerable.Range(1, nombre)
                .Select(i => new LotRappelable($"L{i}", new DateOnly(2030, 1, 1), i <= epuises ? 0 : i, false)).ToList());
        }
    }

    [Fact]
    public async Task Les_lots_epuises_sont_proposes_et_marques_epuises()
    {
        var lots = await new ProposerLotsRappel(new LotsFactices(3, epuises: 1)).ExecuterAsync(7, "l");
        Assert.Equal(3, lots.Count);
        Assert.Equal([true, false, false], lots.Select(l => l.Epuise));
        Assert.Equal(0, lots[0].Quantite);
    }

    [Fact]
    public async Task Dix_lots_au_plus_saisie_sans_espaces_autour()
    {
        var depot = new LotsFactices(25);
        var lots = await new ProposerLotsRappel(depot).ExecuterAsync(7, "  ab12  ");
        Assert.Equal(10, lots.Count);
        Assert.Equal((7L, "ab12", 10), Assert.Single(depot.Appels));
        // Sans saisie : les lots du produit (10 au plus)
        Assert.Equal(10, (await new ProposerLotsRappel(depot).ExecuterAsync(7, null)).Count);
        Assert.Equal("", depot.Appels[^1].Texte);
    }

    [Fact]
    public async Task Sans_produit_rien_n_est_lu()
    {
        var depot = new LotsFactices(3);
        var refus = await Assert.ThrowsAsync<RegleMetierException>(() => new ProposerLotsRappel(depot).ExecuterAsync(0, "ab"));
        Assert.Equal("produit_rappel", refus.Champ);
        Assert.Empty(depot.Appels);
    }
}
