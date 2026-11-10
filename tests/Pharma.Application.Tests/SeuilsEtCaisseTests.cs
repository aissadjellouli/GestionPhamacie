using Pharma.Application.Administration;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Mobile;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class SeuilsStockEnMemoire : ISeuilsStock
{
    public SeuilStockProduit Produit { get; set; } = new(7, "Sintrom 4 mg cp", 20, 0.5m, 5, null, "ok");
    public Task<SeuilStockProduit?> ProduitAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult<SeuilStockProduit?>(produitId == Produit.ProduitId ? Produit : null);
    public Task<IReadOnlyList<SeuilStockProduit>> ManuelsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SeuilStockProduit>>(Produit.SeuilManuel is null ? [] : [Produit]);
    public Task DefinirAsync(long produitId, int? seuil, CancellationToken ct = default)
    {
        Produit = Produit with { SeuilManuel = seuil };
        return Task.CompletedTask;
    }
}

public class SeuilsStockTests
{
    [Fact]
    public void Seuil_saisi_remplace_l_automatique()
    {
        var p = new SeuilStockProduit(1, "X", 3, 1, 9, null, "critique");
        Assert.Equal(9, p.SeuilApplique);
        Assert.Equal(4, (p with { SeuilManuel = 4 }).SeuilApplique);
        Assert.Equal(0, (p with { SeuilManuel = 0 }).SeuilApplique);     // 0 : pas d'alerte
    }

    [Fact]
    public async Task Definir_recalcule_les_alertes_et_journalise_puis_revient_a_l_automatique()
    {
        var (depot, moteur, journal) = (new SeuilsStockEnMemoire(), new MoteurEspion(), new JournalEspion());
        var cas = new DefinirSeuilStock(depot, new DroitsFactices("admin.parametres"), moteur, journal);

        var r = await cas.ExecuterAsync(7, 12, 1);
        Assert.Equal((12, 12), (r.SeuilManuel!.Value, r.SeuilApplique));
        r = await cas.ExecuterAsync(7, null, 1);
        Assert.Equal((null, 5), (r.SeuilManuel, r.SeuilApplique));
        Assert.Equal(2, moteur.Appels);
        Assert.Equal(["seuil_stock", "seuil_stock"], journal.Actions);
    }

    [Fact]
    public async Task Seuil_negatif_ou_sans_droit_refuse()
    {
        var depot = new SeuilsStockEnMemoire();
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new DefinirSeuilStock(depot, new DroitsFactices("admin.parametres"), new MoteurEspion(), new JournalEspion()).ExecuterAsync(7, -1, 1));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new DefinirSeuilStock(depot, new DroitsFactices("caisse.vendre"), new MoteurEspion(), new JournalEspion()).ExecuterAsync(7, 3, 2));
    }
}

public class ControleBoiteCaisseTests
{
    private static ControlerBoiteCaisse Cas(MobileEnMemoire m) =>
        new(m, new DroitsFactices("caisse.vendre"), new HorlogeFixe(MobileEnMemoire.Aujourdhui));

    [Fact]
    public async Task Boite_plus_recente_qu_un_lot_proche_de_la_peremption_avertit()
    {
        var m = new MobileEnMemoire();       // lot 70 (SIN4-26) : dans un an, celui de la boîte scannée
        m.Lots.Add(new(71, "SIN-OLD", MobileEnMemoire.Aujourdhui.AddDays(40), false, 3, false, null, "Grossiste A", "rouge"));
        var b = await Cas(m).ExecuterAsync(MobileEnMemoire.DataMatrix, 3);

        Assert.Equal(70, b!.Lot.LotId);                 // la boîte scannée est servie telle quelle
        Assert.Equal(71, b.LotConseille!.LotId);
        Assert.Contains("SIN-OLD", b.Avertissement);
        Assert.Equal(23, b.StockProduit);
    }

    [Fact]
    public async Task Pas_d_avertissement_si_le_lot_plus_ancien_n_est_pas_proche()
    {
        var m = new MobileEnMemoire();
        m.Lots.Add(new(71, "SIN-OLD", MobileEnMemoire.Aujourdhui.AddDays(300), false, 3, false, null, "Grossiste A", "ok"));
        var b = await Cas(m).ExecuterAsync(MobileEnMemoire.DataMatrix, 3);
        Assert.Null(b!.Avertissement);
    }

    [Fact]
    public async Task Code_fabricant_laisse_la_caisse_servir_le_lot_FEFO()
    {
        Assert.Null(await Cas(new MobileEnMemoire()).ExecuterAsync(MobileEnMemoire.Ean, 3));
    }

    [Fact]
    public async Task Boite_vendue_ou_lot_bloque_refuses()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new MobileEnMemoire { ParDataMatrix = new(7, 70, "boite", "vendue") }).ExecuterAsync(MobileEnMemoire.DataMatrix, 3));
        var m = new MobileEnMemoire();
        m.Lots[0] = m.Lots[0] with { Bloque = true };
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(m).ExecuterAsync(MobileEnMemoire.DataMatrix, 3));
    }
}
