using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>
/// Chargement complet (script 129, docs/conception-synchronisation.md § 13). L'instantané est pris en deux transactions :
/// <list type="number">
/// <item>A (REPEATABLE READ, très courte) : LOCK sync_sortie IN SHARE MODE attend la fin des transactions qui ont déjà inscrit
/// une ligne dans la file et retient les suivantes ; puis l'image (snapshot) est prise et exportée avec le repère
/// = plus grand n° de file visible. Toute modification visible a donc un n° &lt;= repère ; toute modification non visible
/// reçoit un n° &gt; repère (elle n'a pu inscrire sa ligne qu'après la fin de A).</item>
/// <item>B (REPEATABLE READ, image de A importée) : A est validée aussitôt (le verrou ne dure que l'attente des transactions
/// en cours), puis B lit chaque table dans l'image et inscrit les lignes « instantane » (ordre = repère).</item>
/// </list>
/// Les ventes ne sont retenues que pendant l'attente du verrou (2 s au plus, sinon nouvel essai au passage suivant).
/// </summary>
public sealed partial class ChargementSyncSql(NpgsqlDataSource db) : IChargementSyncDepot
{
    public static readonly TimeSpan AttenteVerrouMax = TimeSpan.FromSeconds(2);

    public async Task<ChargementSync?> DernierAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.numero, c.motif, c.etat, c.demande_le, c.epoque, c.repere, coalesce(c.lignes, '{}'::jsonb)::text, c.lignes_total,
                   CASE WHEN c.etat = 'termine' THEN c.lignes_total ELSE (SELECT g.accusees FROM sync_chargement_progression(c.numero) g) END,
                   c.termine_le, c.absentes_central
            FROM sync_chargement c
            ORDER BY (c.etat NOT IN ('termine', 'remplace')) DESC, c.numero DESC LIMIT 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        var lignes = JsonSerializer.Deserialize<Dictionary<string, long>>(r.GetString(6)) ?? [];
        return new ChargementSync(r.GetInt32(0), r.GetString(1), r.GetString(2), Utc(r.GetDateTime(3)), r.IsDBNull(4) ? null : r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetInt64(5), lignes, r.GetInt64(7), r.GetInt64(8), r.IsDBNull(9) ? null : Utc(r.GetDateTime(9)),
            r.IsDBNull(10) ? null : r.GetInt64(10));
    }

    public async Task<int?> DemanderAsync(string motif, long? utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_demander_chargement($1, $2)");
        cmd.Parameters.AddWithValue(motif);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)utilisateurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return await cmd.ExecuteScalarAsync(ct) is int n ? n : null;
    }

    public async Task ConstituerAsync(int numero, Action<int, int>? progression, CancellationToken ct = default)
    {
        try { await ConstituerSansReprise(numero, progression, ct); }
        catch (PostgresException e) when (e.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.LockNotAvailable
                                              or PostgresErrorCodes.DeadlockDetected)
        {
            throw new ChargementReporteException($"Instantané du chargement {numero} reporté (base occupée, {e.SqlState}) : nouvel essai au prochain passage.");
        }
    }

    private async Task ConstituerSansReprise(int numero, Action<int, int>? progression, CancellationToken ct)
    {
        // 0. État « constitution » (plus aucun lot formé) et codes créés d'avance, validés avant l'instantané
        List<string> tables;
        await using (var cmd = db.CreateCommand("SELECT sync_chargement_preparer($1)"))
        {
            cmd.Parameters.AddWithValue(numero);
            await cmd.ExecuteScalarAsync(ct);
        }

        await using var cnxA = await db.OpenConnectionAsync(ct);
        await using var cnxB = await db.OpenConnectionAsync(ct);
        string image;
        long repere;
        // 1. A : verrou court, image exportée, repère
        await using (var txA = await cnxA.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct))
        {
            await using (var c = new NpgsqlCommand(
                $"SET LOCAL lock_timeout = '{(int)AttenteVerrouMax.TotalMilliseconds}ms'; LOCK TABLE sync_sortie IN SHARE MODE", cnxA, txA))
                await c.ExecuteNonQueryAsync(ct);
            await using (var c = new NpgsqlCommand("SELECT pg_export_snapshot(), coalesce((SELECT max(id) FROM sync_sortie), 0)", cnxA, txA))
            await using (var r = await c.ExecuteReaderAsync(ct))
            {
                await r.ReadAsync(ct);
                (image, repere) = (r.GetString(0), r.GetInt64(1));
            }
            if (!ImageValide().IsMatch(image)) throw new InvalidOperationException("Identifiant d'image inattendu.");

            // 2. B importe l'image AVANT la fin de A, puis A est validée (verrou rendu)
            var txB = await cnxB.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await using (txB)
            {
                await using (var c = new NpgsqlCommand($"SET TRANSACTION SNAPSHOT '{image}'", cnxB, txB)) await c.ExecuteNonQueryAsync(ct);
                await txA.CommitAsync(ct);

                await using (var c = new NpgsqlCommand("SELECT t FROM sync_tables_envoyees() t", cnxB, txB))
                await using (var r = await c.ExecuteReaderAsync(ct))
                {
                    tables = [];
                    while (await r.ReadAsync(ct)) tables.Add(r.GetString(0));
                }
                var lignes = new SortedDictionary<string, long>(StringComparer.Ordinal);
                progression?.Invoke(0, tables.Count);
                foreach (var t in tables)
                {
                    await using var c = new NpgsqlCommand("SELECT sync_instantane_table($1, $2, $3)", cnxB, txB) { CommandTimeout = 0 };
                    c.Parameters.AddWithValue(numero);
                    c.Parameters.AddWithValue(t);
                    c.Parameters.AddWithValue(repere);
                    lignes[t] = (long)(await c.ExecuteScalarAsync(ct))!;
                    progression?.Invoke(lignes.Count, tables.Count);
                }
                await using (var c = new NpgsqlCommand("SELECT sync_chargement_pret($1, $2, $3::jsonb)", cnxB, txB))
                {
                    c.Parameters.AddWithValue(numero);
                    c.Parameters.AddWithValue(repere);
                    c.Parameters.AddWithValue(JsonSerializer.Serialize(lignes));
                    await c.ExecuteNonQueryAsync(ct);
                }
                await txB.CommitAsync(ct);
            }
        }
    }

    public async Task MarquerAnnonceAsync(int numero, long reprendreApres, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_chargement_annonce($1, $2)");
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(reprendreApres);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ToutAccuseAsync(int numero, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_chargement_accuse($1)");
        cmd.Parameters.AddWithValue(numero);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task TerminerAsync(int numero, long absentes, IReadOnlyDictionary<string, long> parTable, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_chargement_terminer($1, $2, $3::jsonb)");
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(absentes);
        cmd.Parameters.AddWithValue(JsonSerializer.Serialize(parTable));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);

    [GeneratedRegex("^[0-9A-F]+(-[0-9A-F]+)+$")]
    private static partial Regex ImageValide();
}
