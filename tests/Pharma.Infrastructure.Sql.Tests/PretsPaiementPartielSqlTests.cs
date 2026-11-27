using Pharma.Application.Financements;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;
using Pharma.Infrastructure.Financements;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Prêts (script 170) sur une copie jetable : échéance payée en deux fois (charges d'abord), paiement simultané refusé, remboursement
/// anticipé qui bloque les échéances jusqu'au nouveau tableau, levée du blocage par l'échéancier remplacé.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class PretsPaiementPartielSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private async Task<long> ScalaireAsync(string sql)
    {
        await using var cmd = copie.Verif.CreateCommand(sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [FactSql]
    public async Task Paiement_en_deux_fois_puis_remboursement_anticipe_puis_nouvel_echeancier()
    {
        var sql = new FinancementsSql(copie.Source);
        var titulaire = await copie.Donnees.UtilisateurAsync();
        var coffre = await ScalaireAsync("SELECT id FROM compte_tresorerie WHERE type = 'coffre' ORDER BY id LIMIT 1");
        await using (var cmd = copie.Verif.CreateCommand($"""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, utilisateur_id)
            VALUES ({coffre}, current_date, 'entree', 100000, 'encaissement', 'Essai 170', {titulaire})
            """))
            await cmd.ExecuteNonQueryAsync();
        var pretId = await sql.CreerPretAsync(new PretAEcrire(null, "Prêteur essai 170", "SQL-170", null, 1000, new DateOnly(2026, 1, 1), titulaire));
        LigneEcheancier[] lignes =
        [
            new(new DateOnly(2026, 2, 1), 500, 50, 0, 0, 550, 500),
            new(new DateOnly(2026, 3, 1), 500, 50, 0, 0, 550, 0)
        ];
        await sql.RemplacerEcheancierAsync(pretId, 0, lignes);
        var e1 = (await sql.PretAsync(pretId))!.Echeances[0];

        // 1er paiement : 300 = 50 de charges + 250 de capital ; l'échéance n'est pas payée
        await sql.PayerEcheanceAsync(new PaiementEcheanceAEcrire(e1.Id, new DateOnly(2026, 2, 1), "especes", null, null, 0, null, titulaire, 300, 0));
        var d = (await sql.PretAsync(pretId))!;
        Assert.Equal((300m, 250m, 50m, (DateOnly?)null), (d.Echeances[0].MontantPaye, d.Echeances[0].CapitalPaye, d.Echeances[0].ChargesPayees, d.Echeances[0].PayeeLe));
        Assert.Equal((250m, 750m), (d.Pret.CapitalRembourse, d.Pret.ResteDu));

        // Déjà payé lu avant un autre paiement : refusé
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            sql.PayerEcheanceAsync(new PaiementEcheanceAEcrire(e1.Id, new DateOnly(2026, 2, 2), "especes", null, null, 0, null, titulaire, 250, 0)));
        await sql.PayerEcheanceAsync(new PaiementEcheanceAEcrire(e1.Id, new DateOnly(2026, 2, 2), "especes", null, null, 0, null, titulaire, 250, 300));
        d = (await sql.PretAsync(pretId))!;
        Assert.Equal(new DateOnly(2026, 2, 2), d.Echeances[0].PayeeLe);
        Assert.Equal((500m, 1, 550m), (d.Pret.CapitalRembourse, d.Pret.EcheancesPayees, d.Pret.ProchainMontant!.Value));

        // Remboursement anticipé de 200 (frais 10) : capital remboursé 700, échéances bloquées
        await sql.RembourserParAnticipationAsync(new RemboursementAnticipeAEcrire(pretId, new DateOnly(2026, 2, 10), 200, 10, "especes", null, null, 0, null,
            titulaire, 500));
        d = (await sql.PretAsync(pretId))!;
        Assert.Equal((700m, 300m, true, 200m), (d.Pret.CapitalRembourse, d.Pret.ResteDu, d.Pret.EcheancierARemplacer, d.Pret.CapitalAnticipe));
        Assert.Equal((200m, 10m), (Assert.Single(d.Anticipes!).Capital, d.Anticipes![0].Frais));
        var e2 = d.Echeances[1];
        var refus = await Assert.ThrowsAsync<RegleMetierException>(() =>
            sql.PayerEcheanceAsync(new PaiementEcheanceAEcrire(e2.Id, new DateOnly(2026, 3, 1), "especes", null, null, 0, null, titulaire, 550, 0)));
        Assert.Contains("nouveau tableau", refus.Message);

        // Nouveau tableau : l'échéance payée en tête, puis 300 de capital ; le blocage est levé
        await sql.RemplacerEcheancierAsync(pretId, 1, [new LigneEcheancier(new DateOnly(2026, 3, 1), 300, 20, 0, 0, 320, 0)]);
        d = (await sql.PretAsync(pretId))!;
        Assert.False(d.Pret.EcheancierARemplacer);
        Assert.Equal((2, 320m), (d.Echeances.Count, d.Echeances[1].Ligne.Montant));
        await sql.PayerEcheanceAsync(new PaiementEcheanceAEcrire(d.Echeances[1].Id, new DateOnly(2026, 3, 1), "especes", null, null, 0, null, titulaire, 320, 0));
        d = (await sql.PretAsync(pretId))!;
        Assert.Equal((1000m, 0m, 2), (d.Pret.CapitalRembourse, d.Pret.ResteDu, d.Pret.EcheancesPayees));
        // Charges financières : 50 + 10 de frais + 20 ; capital : 1 000, tous sortis du coffre
        Assert.Equal(80, await ScalaireAsync($"SELECT sum(montant) FROM mouvement_tresorerie WHERE categorie = 'charges_financieres' AND libelle LIKE '%SQL-170%'"));
        Assert.Equal(1000, await ScalaireAsync($"SELECT sum(montant) FROM mouvement_tresorerie WHERE nature = 'remboursement_financement' AND libelle LIKE '%SQL-170%'"));
    }

    [FactSql]
    public async Task Remboursement_anticipe_qui_solde_le_pret_leve_le_blocage_de_l_echeancier()
    {
        var sql = new FinancementsSql(copie.Source);
        var titulaire = await copie.Donnees.UtilisateurAsync();
        var coffre = await ScalaireAsync("SELECT id FROM compte_tresorerie WHERE type = 'coffre' ORDER BY id LIMIT 1");
        await using (var cmd = copie.Verif.CreateCommand($"""
            INSERT INTO mouvement_tresorerie (compte_id, date_mouvement, sens, montant, nature, libelle, utilisateur_id)
            VALUES ({coffre}, current_date, 'entree', 100000, 'encaissement', 'Essai solde anticipé', {titulaire})
            """))
            await cmd.ExecuteNonQueryAsync();
        var pretId = await sql.CreerPretAsync(new PretAEcrire(null, "Prêteur essai solde", "SQL-SOLDE", null, 1000, new DateOnly(2026, 1, 1), titulaire));
        await sql.RemplacerEcheancierAsync(pretId, 0,
        [
            new LigneEcheancier(new DateOnly(2026, 2, 1), 500, 50, 0, 0, 550, 500),
            new LigneEcheancier(new DateOnly(2026, 3, 1), 500, 50, 0, 0, 550, 0)
        ]);

        // Anticipé partiel (200) : échéancier à remplacer ; puis anticipé du reste (800) : prêt soldé, blocage levé, échéances retirées
        await sql.RembourserParAnticipationAsync(new RemboursementAnticipeAEcrire(pretId, new DateOnly(2026, 1, 10), 200, 0, "especes", null, null, 0, null,
            titulaire, 0));
        Assert.True((await sql.PretAsync(pretId))!.Pret.EcheancierARemplacer);
        await sql.RembourserParAnticipationAsync(new RemboursementAnticipeAEcrire(pretId, new DateOnly(2026, 1, 20), 800, 0, "especes", null, null, 0, null,
            titulaire, 200));
        var d = (await sql.PretAsync(pretId))!;
        Assert.Equal((1000m, 0m, false), (d.Pret.CapitalRembourse, d.Pret.ResteDu, d.Pret.EcheancierARemplacer));
        Assert.Empty(d.Echeances);
    }
}
