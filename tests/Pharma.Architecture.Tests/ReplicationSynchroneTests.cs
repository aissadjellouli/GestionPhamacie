using Npgsql;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot V2-7 : réplication synchrone par transaction. Unitaires (chien de garde) et intégration sur base jetable vide (sans 2e serveur,
/// donc sans effet : aucun SET n'est envoyé ; SET LOCAL est accepté et retombe à la fin de la transaction). Sans PostgreSQL joignable,
/// les tests de base ne font rien.
/// </summary>
public class ReplicationSynchroneTests
{
    [Fact]
    public async Task Validation_rapide_n_annule_rien()
    {
        var appels = 0;
        await ReplicationSynchrone.AttendreBorneAsync(Task.CompletedTask, TimeSpan.FromSeconds(5), _ => { appels++; return Task.FromResult(true); }, default);
        Assert.Equal(0, appels);
    }

    [Fact]
    public async Task Validation_bloquee_est_annulee_apres_le_delai_puis_termine_et_alerte()
    {
        var fin = new TaskCompletionSource();
        var annulations = 0;
        var alertes = 0;
        void Ecoute(TimeSpan _) => Interlocked.Increment(ref alertes);
        ReplicationSynchrone.AttenteAbandonnee += Ecoute;
        var avant = ReplicationSynchrone.Abandons;
        try
        {
            await ReplicationSynchrone.AttendreBorneAsync(fin.Task, TimeSpan.FromMilliseconds(50), _ =>
            {
                annulations++;
                fin.SetResult();   // le serveur lève l'attente et la validation se termine
                return Task.FromResult(true);
            }, default);
        }
        finally { ReplicationSynchrone.AttenteAbandonnee -= Ecoute; }
        Assert.Equal(1, annulations);
        Assert.Equal(1, alertes);
        Assert.Equal(avant + 1, ReplicationSynchrone.Abandons);
    }

    [Fact]
    public async Task Validation_lente_sans_attente_synchrone_a_annuler_ne_leve_pas_d_alerte()
    {
        var fin = Task.Delay(150);
        var avant = ReplicationSynchrone.Abandons;
        await ReplicationSynchrone.AttendreBorneAsync(fin, TimeSpan.FromMilliseconds(30), _ => Task.FromResult(false), default);
        Assert.Equal(avant, ReplicationSynchrone.Abandons);
    }

    [Fact]
    public async Task Officine_a_un_seul_serveur_aucun_SET_n_est_envoye_et_la_validation_est_normale()
    {
        using var base_ = new BaseJetableM4("pharmacie_jetable_v27_", false);
        if (!base_.Disponible) return;
        await using var cx = await base_.Source!.OpenConnectionAsync();
        await using (var t = new NpgsqlCommand("CREATE TABLE essai (n int)", cx)) await t.ExecuteNonQueryAsync();
        Assert.Equal(0L, await ScalaireAsync<long>(cx, null, "SELECT count(*) FROM pg_stat_replication"));

        await using var tx = await cx.BeginTransactionAsync();
        var defaut = await ScalaireAsync<string>(cx, tx, "SELECT current_setting('synchronous_commit')");
        var synchrone = await ReplicationSynchrone.ActiverAsync(cx, tx);
        Assert.False(synchrone);
        Assert.Equal(defaut, await ScalaireAsync<string>(cx, tx, "SELECT current_setting('synchronous_commit')"));
        await using (var i = new NpgsqlCommand("INSERT INTO essai VALUES (1)", cx, tx)) await i.ExecuteNonQueryAsync();
        await ReplicationSynchrone.ValiderAsync(base_.Source!, cx, tx, synchrone);
        Assert.Equal(1L, await ScalaireAsync<long>(cx, null, "SELECT count(*) FROM essai"));
    }

    [Fact]
    public async Task SET_LOCAL_remote_write_est_accepte_sans_effet_sans_standby_et_retombe_a_la_fin_de_la_transaction()
    {
        using var base_ = new BaseJetableM4("pharmacie_jetable_v27_", false);
        if (!base_.Disponible) return;
        await using var cx = await base_.Source!.OpenConnectionAsync();
        var defaut = await ScalaireAsync<string>(cx, null, "SELECT current_setting('synchronous_commit')");
        await using var tx = await cx.BeginTransactionAsync();
        await using (var s = new NpgsqlCommand("SET LOCAL synchronous_commit = remote_write", cx, tx)) await s.ExecuteNonQueryAsync();
        Assert.Equal("remote_write", await ScalaireAsync<string>(cx, tx, "SELECT current_setting('synchronous_commit')"));
        // Validation synchrone sans standby : rend la main tout de suite (aucune attente), le délai n'est pas atteint
        var debut = DateTime.UtcNow;
        await ReplicationSynchrone.ValiderAsync(base_.Source!, cx, tx, true);
        Assert.True(DateTime.UtcNow - debut < ReplicationSynchrone.DelaiMaximal);
        Assert.Equal(defaut, await ScalaireAsync<string>(cx, null, "SELECT current_setting('synchronous_commit')"));
    }

    [Fact]
    public void Suspension_apres_abandon_garde_la_position_la_plus_avancee()
    {
        // Essai réel du 2026-11-25 (réseau figé) : après un abandon, le 2e serveur doit avoir écrit au moins jusqu'à cette position
        ReplicationSynchrone.Suspendre(new NpgsqlTypes.NpgsqlLogSequenceNumber(0xFFFF_0000_0000_0010));
        ReplicationSynchrone.Suspendre(new NpgsqlTypes.NpgsqlLogSequenceNumber(0xFFFF_0000_0000_0001));
        Assert.True(ReplicationSynchrone.SuspenduJusqua >= new NpgsqlTypes.NpgsqlLogSequenceNumber(0xFFFF_0000_0000_0010));
    }

    [Fact]
    public async Task Suspendu_sans_2e_serveur_qui_repond_aucun_SET_et_la_suspension_reste()
    {
        using var base_ = new BaseJetableM4("pharmacie_jetable_v27_", false);
        if (!base_.Disponible) return;
        ReplicationSynchrone.Suspendre(new NpgsqlTypes.NpgsqlLogSequenceNumber(0xFFFF_0000_0000_0020));
        await using var cx = await base_.Source!.OpenConnectionAsync();
        await using var tx = await cx.BeginTransactionAsync();
        var defaut = await ScalaireAsync<string>(cx, tx, "SELECT current_setting('synchronous_commit')");
        Assert.False(await ReplicationSynchrone.ActiverAsync(cx, tx));   // requête « le 2e serveur répond » acceptée par PostgreSQL
        Assert.Equal(defaut, await ScalaireAsync<string>(cx, tx, "SELECT current_setting('synchronous_commit')"));
        Assert.NotNull(ReplicationSynchrone.SuspenduJusqua);
        await tx.RollbackAsync();
    }

    [Fact]
    public void Abandon_note_la_position_du_principal_et_la_reprise_exige_le_2e_serveur_a_jour()
    {
        var source = File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Infrastructure", "SecondServeur", "ReplicationSynchrone.cs"));
        Assert.Contains("if (annule) await SuspendreApresAbandonAsync(db, c);", source);
        Assert.Contains("SELECT pg_current_wal_lsn()", source);
        Assert.Contains("WHERE write_lsn >= $1", source);
    }

    private static async Task<T> ScalaireAsync<T>(NpgsqlConnection cx, NpgsqlTransaction? tx, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }
}
