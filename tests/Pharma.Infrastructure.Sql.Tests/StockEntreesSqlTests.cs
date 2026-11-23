using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Achats;
using Pharma.Infrastructure.Lecture;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Lot 3 : correction d'entrée sous garde de quantité (S7) et boîtes manquantes à la réception (S8).</summary>
[Trait("Categorie", "Sql")]
public sealed class StockEntreesSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private static CorrectionAEcrire Correction(long lot, int quantiteAvant, int? quantite, long utilisateur, bool bloquer = false) =>
        new(lot, quantiteAvant, null, null, null, null, quantite, "Essai de correction", utilisateur,
            quantite is null ? [] : [new ChampCorrige("quantite", quantiteAvant.ToString(), quantite.ToString())], bloquer);

    [FactSql]
    public async Task S7_Correction_refusee_quand_la_quantite_a_bouge_depuis_la_lecture()
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 10);
        await new SortiesStockSql(copie.Source).SortirAsync(new SortieStockAEcrire(lot, 1, "casse", "Essai", utilisateur));   // 10 -> 9
        var sut = new EntreesStockSql(copie.Source);

        await Assert.ThrowsAsync<RegleMetierException>(() => sut.CorrigerAsync(Correction(lot, quantiteAvant: 10, quantite: 8, utilisateur)));

        Assert.Equal(9, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM correction_lot WHERE lot_id = @l", ("l", lot)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'ajustement_inventaire'", ("l", lot)));
    }

    [FactSql]
    public async Task S7_Correction_stale_avec_blocage_ne_bloque_pas_le_lot()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 10);
        await d.ExecuterAsync("UPDATE lot SET quantite_disponible = 9 WHERE id = @l", ("l", lot));
        var sut = new EntreesStockSql(copie.Source);

        await Assert.ThrowsAsync<RegleMetierException>(() => sut.CorrigerAsync(Correction(lot, quantiteAvant: 10, quantite: null, utilisateur, bloquer: true)));

        Assert.False(await d.LireAsync<bool>("SELECT bloque FROM lot WHERE id = @l", ("l", lot)));
    }

    [FactSql]
    public async Task S7_Correction_avec_BloquerLot_pose_le_blocage_et_le_motif()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 10);
        var sut = new EntreesStockSql(copie.Source);

        await sut.CorrigerAsync(Correction(lot, quantiteAvant: 10, quantite: null, utilisateur, bloquer: true));

        Assert.True(await d.LireAsync<bool>("SELECT bloque FROM lot WHERE id = @l", ("l", lot)));
        Assert.Contains("bloqué à la correction d'entrée", await d.LireAsync<string>("SELECT motif_blocage FROM lot WHERE id = @l", ("l", lot)));
    }

    [FactSql]
    public async Task S7_Correction_a_jour_ecrit_le_lot_le_mouvement_et_la_trace()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lot = await d.LotAsync(await d.ProduitAsync(), 10);
        var sut = new EntreesStockSql(copie.Source);

        await sut.CorrigerAsync(Correction(lot, quantiteAvant: 10, quantite: 8, utilisateur));

        Assert.Equal(8, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
        Assert.Equal(-2, await d.LireAsync<int>("SELECT quantite FROM mouvement_stock WHERE lot_id = @l AND type = 'ajustement_inventaire'", ("l", lot)));
        Assert.Equal(1, await d.LireAsync<long>("SELECT count(*) FROM correction_lot WHERE lot_id = @l AND champ = 'quantite'", ("l", lot)));
    }

    [FactSql]
    public async Task S8_Reception_refusee_quand_plus_de_boites_manquantes_que_de_boites_recues()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var fournisseur = await d.FournisseurAsync();
        var produit = await d.ProduitAsync();
        var bl = $"BL-ESSAI-S8-{Donnees.Suivant()}";
        var sut = new AchatsSql(copie.Source);

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(
            () => sut.ReceptionnerAsync(Donnees.ReceptionEssai(fournisseur, utilisateur, produit, quantite: 2, bl, boitesManquantes: 3)));

        Assert.Contains("Réception annulée", erreur.Message);
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM reception WHERE numero_bl = @bl", ("bl", bl)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM lot WHERE produit_id = @p", ("p", produit)));
        Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM boite b JOIN lot l ON l.id = b.lot_id WHERE l.produit_id = @p", ("p", produit)));
        Assert.Equal(0m, await d.LireAsync<decimal>("SELECT solde FROM fournisseur WHERE id = @f", ("f", fournisseur)));
    }

    [FactSql]
    public async Task S8_Reception_acceptee_avec_un_manquant_sort_la_boite_du_stock()
    {
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var fournisseur = await d.FournisseurAsync();
        var produit = await d.ProduitAsync();
        var sut = new AchatsSql(copie.Source);

        await sut.ReceptionnerAsync(Donnees.ReceptionEssai(fournisseur, utilisateur, produit, quantite: 2, $"BL-ESSAI-S8-{Donnees.Suivant()}", boitesManquantes: 1));

        Assert.Equal(1, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE produit_id = @p", ("p", produit)));
        Assert.Equal(1, await d.LireAsync<long>(
            "SELECT count(*) FROM boite b JOIN lot l ON l.id = b.lot_id WHERE l.produit_id = @p AND b.statut = 'manquante'", ("p", produit)));
    }
}
