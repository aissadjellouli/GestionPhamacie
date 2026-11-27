using Npgsql;
using Pharma.Application.Offre;
using Pharma.Infrastructure.Offre;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Liaison renforcée de l'offre (point 5, corrections du 2026-11-27) sur une copie jetable : dernière date du journal fiscal, alertes
/// d'horloge (reculée, en avance) notées une fois par date plancher, marque de refus levée quand un code repasse le contrôle ; rien ne
/// bloque sur une base en lecture seule.
/// </summary>
[Trait("Categorie", "Sql")]
public sealed class ActivationsOffreSqlTests(CopieSql copie) : IClassFixture<CopieSql>
{
    private NpgsqlDataSource LectureSeule() => NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion())
    {
        Database = copie.Nom, Options = "-c default_transaction_read_only=on"
    }.ConnectionString);

    private async Task<long> CompterAsync(string action, long? enregistrement = null)
    {
        await using var cmd = copie.Verif.CreateCommand(
            "SELECT count(*) FROM journal_audit WHERE action = $1 AND ($2::bigint IS NULL OR enregistrement_id = $2)");
        cmd.Parameters.AddWithValue(action);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)enregistrement ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bigint });
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    private async Task<DateTime> MaintenantBaseAsync()
    {
        await using var cmd = copie.Verif.CreateCommand("SELECT now()");
        return DateTime.SpecifyKind((DateTime)(await cmd.ExecuteScalarAsync())!, DateTimeKind.Utc);
    }

    private async Task EvenementFiscalAsync(DateTime survenuUtc)
    {
        await using var cmd = copie.Verif.CreateCommand("INSERT INTO journal_vente (evenement, survenu_le, poste) VALUES ('ouverture', $1, 'essai-offre')");
        cmd.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.TimestampTz, survenuUtc);
        await cmd.ExecuteNonQueryAsync();
    }

    [FactSql]
    public async Task Derniere_date_fiscale_la_plus_grande_des_dates_des_faits_et_de_l_enregistrement_de_la_derniere_ligne()
    {
        var sql = new ActivationsOffreSql(copie.Source);
        await using (var cmd = copie.Verif.CreateCommand("SELECT count(*) FROM journal_vente"))
            if ((long)(await cmd.ExecuteScalarAsync())! == 0) Assert.Null(await sql.DerniereDateFiscaleAsync());   // journal vide : aucune date

        // Fait ancien enregistré maintenant : c'est la date d'enregistrement (posée par la base) qui compte
        var avant = await MaintenantBaseAsync();
        await EvenementFiscalAsync(new DateTime(2020, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var d1 = await sql.DerniereDateFiscaleAsync();
        Assert.Equal(DateTimeKind.Utc, d1!.Value.Kind);
        Assert.InRange(d1.Value, avant.AddSeconds(-1), avant.AddMinutes(5));

        // Fait daté plus loin (horloge du serveur en avance au moment de la vente) : il reste la date plancher, même après un fait plus ancien
        var futur = new DateTime(2031, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        await EvenementFiscalAsync(futur);
        await EvenementFiscalAsync(new DateTime(2021, 6, 1, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal(futur, await sql.DerniereDateFiscaleAsync());
    }

    [FactSql]
    public async Task Horloge_reculee_ou_en_avance_notee_une_seule_fois_par_date_plancher_et_jamais_bloquante()
    {
        var sql = new ActivationsOffreSql(copie.Source);
        var plancher = new DateTime(2030, 5, 4, 3, 2, 1, DateTimeKind.Utc);
        var serveur = new DateTime(2030, 4, 1, 8, 0, 0, DateTimeKind.Utc);
        Assert.True(await sql.SignalerHorlogeReculeeAsync(plancher, serveur));
        Assert.False(await sql.SignalerHorlogeReculeeAsync(plancher.AddMilliseconds(400), serveur.AddHours(1)));   // même seconde : déjà noté
        Assert.True(await sql.SignalerHorlogeReculeeAsync(plancher.AddDays(1), serveur));
        Assert.Equal(2, await CompterAsync(OffreCourante.ActionHorlogeReculee));
        // Détails : date plancher et date du serveur, sans utilisateur
        await using (var cmd = copie.Verif.CreateCommand(
            "SELECT details ->> 'plancher', details ->> 'serveur', utilisateur_id IS NULL FROM journal_audit WHERE action = $1 ORDER BY id LIMIT 1"))
        {
            cmd.Parameters.AddWithValue(OffreCourante.ActionHorlogeReculee);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(("2030-05-04T03:02:01Z", "2030-04-01T08:00:00Z", true), (r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        }

        // En avance : action à part (même date plancher notée une fois de plus, pour l'autre alerte)
        Assert.True(await sql.SignalerHorlogeAvanceAsync(plancher, serveur.AddYears(1)));
        Assert.False(await sql.SignalerHorlogeAvanceAsync(plancher, serveur.AddYears(2)));
        Assert.Equal(1, await CompterAsync(OffreCourante.ActionHorlogeAvance));
        // Simultanés : un seul passe
        var essais = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sql.SignalerHorlogeAvanceAsync(plancher.AddDays(9), serveur)));
        Assert.Equal(1, essais.Count(e => e));

        // Base en lecture seule (serveur écarté, 2e serveur en veille) : rien n'est noté, aucune erreur
        await using var ls = LectureSeule();
        var lecture = new ActivationsOffreSql(ls);
        Assert.False(await lecture.SignalerHorlogeReculeeAsync(plancher.AddDays(30), serveur));
        Assert.False(await lecture.SignalerHorlogeAvanceAsync(plancher.AddDays(30), serveur));
        Assert.Equal(2, await CompterAsync(OffreCourante.ActionHorlogeReculee));
        Assert.Equal(2, await CompterAsync(OffreCourante.ActionHorlogeAvance));
    }

    [FactSql]
    public async Task La_marque_de_refus_se_leve_une_fois_avec_le_journal_et_un_nouveau_refus_repart_de_maintenant()
    {
        var sql = new ActivationsOffreSql(copie.Source);
        var code = "PO1.essai-levee-refus." + Guid.NewGuid().ToString("N");
        Assert.True(await sql.AjouterAsync(code, new DateTime(2026, 11, 20, 8, 0, 0, DateTimeKind.Utc), "code", null, null));
        var id = (await sql.ListerAsync()).Single(a => a.Code == code).Id;

        Assert.False(await sql.LeverRefusAsync(id));                                  // aucune marque : rien à lever, rien au journal
        Assert.Equal(0, await CompterAsync(OffreCourante.ActionRefusLeve, id));
        Assert.True(await sql.SignalerRefusAsync(id, "Ce serveur n’est pas déclaré dans le code de l’offre"));
        var premier = (await sql.ListerAsync()).Single(a => a.Id == id).RefuseeLeUtc;
        Assert.NotNull(premier);

        Assert.True(await sql.LeverRefusAsync(id));
        var levee = (await sql.ListerAsync()).Single(a => a.Id == id);
        Assert.Null(levee.RefuseeLeUtc);
        Assert.Null(levee.MotifRefus);
        Assert.False(await sql.LeverRefusAsync(id));                                  // une seule fois
        Assert.Equal(1, await CompterAsync(OffreCourante.ActionRefusLeve, id));
        await using (var cmd = copie.Verif.CreateCommand(
            "SELECT details ->> 'motif', details ->> 'refusee_le', table_cible, utilisateur_id IS NULL FROM journal_audit WHERE action = $1 AND enregistrement_id = $2"))
        {
            cmd.Parameters.AddWithValue(OffreCourante.ActionRefusLeve);
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(("Ce serveur n’est pas déclaré dans le code de l’offre", "activation_offre", true), (r.GetString(0), r.GetString(2), r.GetBoolean(3)));
            Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$", r.GetString(1));
        }

        // Nouveau refus : nouvelle constatation (nouvelle date, nouvelle ligne au journal), donc nouveau délai de grâce
        Assert.True(await sql.SignalerRefusAsync(id, "Ce serveur n’est pas déclaré dans le code de l’offre"));
        Assert.True((await sql.ListerAsync()).Single(a => a.Id == id).RefuseeLeUtc >= premier);
        Assert.Equal(2, await CompterAsync("offre_activation_refusee", id));

        // Base en lecture seule : rien n'est fait, aucune erreur
        await using var ls = LectureSeule();
        Assert.False(await new ActivationsOffreSql(ls).LeverRefusAsync(id));
        Assert.NotNull((await sql.ListerAsync()).Single(a => a.Id == id).RefuseeLeUtc);
    }
}
