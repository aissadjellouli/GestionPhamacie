using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Infrastructure.Lecture;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Lot 3 : clôture et comptage d'inventaire (S1 à S3) sur une copie jetable.</summary>
[Trait("Categorie", "Sql")]
public sealed class StockInventairesSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    /// <summary>Inventaire de deux lots (stock 5, compté 4 et 7) et décisions « ajusté » calculées sur cette lecture.</summary>
    private async Task<(long Utilisateur, Donnees.InventaireEssai Inventaire, long[] Lots, DecisionLigne[] Decisions)> PreparerAsync()
    {
        sortie.WriteLine($"Copie : {copie.Nom}");
        var d = copie.Donnees;
        var utilisateur = await d.UtilisateurAsync();
        var lotA = await d.LotAsync(await d.ProduitAsync(), 5);
        var lotB = await d.LotAsync(await d.ProduitAsync(), 5);
        var inv = await d.InventaireAsync(utilisateur, (lotA, 4), (lotB, 7));
        var decisions = new[]
        {
            new DecisionLigne(inv.Lignes[0], -1, -100m, DecisionEcart.Ajuste, Compte: 4, Theorique: 5),
            new DecisionLigne(inv.Lignes[1], 2, 200m, DecisionEcart.Ajuste, Compte: 7, Theorique: 5),
        };
        return (utilisateur, inv, [lotA, lotB], decisions);
    }

    private async Task VerifierEtatInchangeAsync(long utilisateur, Donnees.InventaireEssai inv, long[] lots, int compteLigne0)
    {
        var d = copie.Donnees;
        Assert.Equal("en_cours", await d.LireAsync<string>("SELECT statut FROM inventaire WHERE id = @i", ("i", inv.Id)));
        foreach (var lot in lots)
        {
            Assert.Equal(5, await d.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lot)));
            Assert.Equal(0, await d.LireAsync<long>("SELECT count(*) FROM mouvement_stock WHERE lot_id = @l AND type = 'ajustement_inventaire'", ("l", lot)));
        }
        Assert.Equal(compteLigne0, await d.LireAsync<int>("SELECT compte FROM ligne_inventaire WHERE id = @l", ("l", inv.Lignes[0])));
        Assert.Equal(2, await d.LireAsync<long>(
            "SELECT count(*) FROM ligne_inventaire WHERE inventaire_id = @i AND statut = 'compte' AND ecart IS NULL", ("i", inv.Id)));
        _ = utilisateur;
    }

    [FactSql]
    public async Task S1_Terminer_refuse_quand_un_comptage_a_change_depuis_le_calcul_des_decisions()
    {
        var (utilisateur, inv, lots, decisions) = await PreparerAsync();
        var sut = new InventairesSql(copie.Source);
        await sut.EnregistrerComptageAsync(inv.Lignes[0], 3, null, utilisateur);   // le compte passe de 4 à 3 après le calcul

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => sut.TerminerAsync(inv.Id, decisions, utilisateur));

        Assert.Contains("Un comptage a changé", erreur.Message);
        await VerifierEtatInchangeAsync(utilisateur, inv, lots, compteLigne0: 3);
    }

    [FactSql]
    public async Task S1_Terminer_nominal_ajuste_les_lots_et_termine_l_inventaire()
    {
        var (utilisateur, inv, lots, decisions) = await PreparerAsync();
        var sut = new InventairesSql(copie.Source);

        await sut.TerminerAsync(inv.Id, decisions, utilisateur);

        Assert.Equal("termine", await copie.Donnees.LireAsync<string>("SELECT statut FROM inventaire WHERE id = @i", ("i", inv.Id)));
        Assert.Equal(4, await copie.Donnees.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lots[0])));
        Assert.Equal(7, await copie.Donnees.LireAsync<int>("SELECT quantite_disponible FROM lot WHERE id = @l", ("l", lots[1])));
    }

    [FactSql]
    public async Task S2_Terminer_attend_le_comptage_en_cours_puis_refuse_si_le_compte_a_change()
    {
        var (utilisateur, inv, lots, decisions) = await PreparerAsync();
        var sut = new InventairesSql(copie.Source);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        // Comptage en cours : l'inventaire est tenu FOR SHARE (comme EnregistrerComptageAsync)
        await bloqueur.ExecuterAsync("SELECT 1 FROM inventaire WHERE id = @i FOR SHARE", ("i", inv.Id));

        var cloture = Task.Run(() => sut.TerminerAsync(inv.Id, decisions, utilisateur));
        await bloqueur.AttendreBloquesAsync(1, TimeSpan.FromSeconds(5));
        Assert.False(cloture.IsCompleted, "la clôture aurait dû attendre le comptage en cours");
        await bloqueur.ExecuterAsync("UPDATE ligne_inventaire SET compte = 3, theorique = 5 WHERE id = @l", ("l", inv.Lignes[0]));
        await bloqueur.ValiderAsync();

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => cloture.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("Un comptage a changé", erreur.Message);
        await VerifierEtatInchangeAsync(utilisateur, inv, lots, compteLigne0: 3);
    }

    [FactSql]
    public async Task S3_Comptage_refuse_sur_un_inventaire_termine()
    {
        var (utilisateur, inv, _, _) = await PreparerAsync();
        await copie.Donnees.ExecuterAsync("UPDATE inventaire SET statut = 'termine', termine_le = now(), termine_par = @u WHERE id = @i",
            ("u", utilisateur), ("i", inv.Id));
        var sut = new InventairesSql(copie.Source);

        var erreur = await Assert.ThrowsAsync<RegleMetierException>(() => sut.EnregistrerComptageAsync(inv.Lignes[0], 9, null, utilisateur));

        Assert.Contains("plus en cours", erreur.Message);
        Assert.Equal(4, await copie.Donnees.LireAsync<int>("SELECT compte FROM ligne_inventaire WHERE id = @l", ("l", inv.Lignes[0])));
        Assert.Equal("compte", await copie.Donnees.LireAsync<string>("SELECT statut FROM ligne_inventaire WHERE id = @l", ("l", inv.Lignes[0])));
    }
}
