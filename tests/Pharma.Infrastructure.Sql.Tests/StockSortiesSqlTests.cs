using Npgsql;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Lecture;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Lot 3 : sorties de stock (don refusé sur lot bloqué ou périmé, garde de quantité sous verrou), S4 à S6.</summary>
[Trait("Categorie", "Sql")]
public sealed class StockSortiesSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private static SortieStockAEcrire Sortie(long lot, int quantite, string motif, long utilisateur) =>
        new(lot, quantite, motif, "Essai " + motif, utilisateur);

    private Task<int> QuantiteAsync(long lot) => copie.Donnees.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot));

    private Task<long> MouvementsAsync(long lot) => copie.Donnees.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND quantite < 0", ("l", lot));

    [FactSql]
    public async Task S4_Don_refuse_sur_lot_bloque_et_sur_lot_perime_accepte_sur_lot_sain()
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var aujourdhui = DateOnly.FromDateTime(await d.LireAsync<DateTime>("SELECT current_date::timestamp"));
        var bloque = await d.LotAsync(await d.ProduitAsync(), 5, bloque: true);
        var perime = await d.LotAsync(await d.ProduitAsync(), 5, peremption: aujourdhui);
        var sain = await d.LotAsync(await d.ProduitAsync(), 5);
        var sut = new SortiesStockSql(copie.Source);

        var refusBloque = await Assert.ThrowsAsync<RegleMetierException>(() => sut.SortirAsync(Sortie(bloque, 1, "echantillon", utilisateur)));
        var refusPerime = await Assert.ThrowsAsync<RegleMetierException>(() => sut.SortirAsync(Sortie(perime, 1, "echantillon", utilisateur)));
        await sut.SortirAsync(Sortie(sain, 1, "echantillon", utilisateur));

        Assert.Equal("motif_sortie", refusBloque.Champ);
        Assert.Contains("bloqué", refusBloque.Message);
        Assert.Equal("motif_sortie", refusPerime.Champ);
        Assert.Contains("périmé", refusPerime.Message);
        Assert.Equal(5, await QuantiteAsync(bloque));
        Assert.Equal(5, await QuantiteAsync(perime));
        Assert.Equal(0, await MouvementsAsync(bloque) + await MouvementsAsync(perime));
        Assert.Equal(4, await QuantiteAsync(sain));
        Assert.Equal(1, await MouvementsAsync(sain));
    }

    [FactSql]
    public async Task S5_Casse_acceptee_sur_lot_bloque_le_controle_ne_vise_que_le_don()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 5, bloque: true);
        var sut = new SortiesStockSql(copie.Source);

        await sut.SortirAsync(Sortie(lot, 2, "casse", utilisateur));

        Assert.Equal(3, await QuantiteAsync(lot));
        Assert.Equal(1, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'casse' AND quantite = -2", ("l", lot)));
    }

    [FactSql]
    public async Task S6_Deux_sorties_de_3_sur_un_lot_de_5_une_seule_reussit_stock_jamais_negatif()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 5);
        var sut = new SortiesStockSql(copie.Source);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.ExecuterAsync("SELECT 1 FROM lot WHERE id = @l FOR UPDATE", ("l", lot));

        var sorties = new[]
        {
            Task.Run(() => sut.SortirAsync(Sortie(lot, 3, "casse", utilisateur))),
            Task.Run(() => sut.SortirAsync(Sortie(lot, 3, "casse", utilisateur))),
        };
        await bloqueur.AttendreBloquesAsync(2, TimeSpan.FromSeconds(5));
        Assert.All(sorties, t => Assert.False(t.IsCompleted, "la sortie aurait dû attendre le verrou du lot"));
        await bloqueur.ValiderAsync();

        var reussies = 0;
        var refus = 0;
        foreach (var t in sorties)
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(10)); reussies++; }
            catch (RegleMetierException) { refus++; }
            catch (PostgresException e) { Assert.Fail($"erreur SQL {e.SqlState} : {e.MessageText}"); }
        }

        Assert.Equal((1, 1), (reussies, refus));
        Assert.Equal(2, await QuantiteAsync(lot));
        Assert.Equal(1, await MouvementsAsync(lot));
    }
}
