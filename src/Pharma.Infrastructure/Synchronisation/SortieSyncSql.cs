using Npgsql;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>File de synchronisation et lots (script 126 : sync_parametre, sync_sortie, sync_enregistrer_lot).</summary>
public sealed class SortieSyncSql(NpgsqlDataSource db) : ISortieSyncDepot
{
    public async Task<EtatSync> EtatAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.active, p.officine_id, p.epoque, p.version_contrat, p.dernier_lot, p.dernier_accuse, p.lot_max_lignes, p.lot_max_octets,
                   (SELECT count(*) FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque),
                   sync_colonnes_interdites()
            FROM sync_parametre p WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        return new EtatSync(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
            r.GetInt32(6), r.GetInt32(7), r.GetInt64(8), r.GetFieldValue<string[]>(9));
    }

    public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> colonnesRetirees, CancellationToken ct = default) =>
        LireEnAttenteAsync(maximum, colonnesRetirees, null, ct);

    /// <summary>Lignes en attente des seules tables listées (sans le pack G : résultats d'ordres, décision du titulaire du 2026-11-27).</summary>
    public Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, IReadOnlyList<string> colonnesRetirees, IReadOnlyList<string> tables,
        CancellationToken ct = default) =>
        LireEnAttenteAsync(maximum, colonnesRetirees, tables, ct);

    private async Task<IReadOnlyList<LigneSortieSync>> LireEnAttenteAsync(int maximum, IReadOnlyList<string> colonnesRetirees, IReadOnlyList<string>? tables,
        CancellationToken ct)
    {
        // Colonnes des familles non autorisées retirées ici (scripts 130 et 136), pas dans la file : une ligne déjà en file est filtrée aussi
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.nom_table, s.id_local, s.operation, s.survenu_le, sync_retirer_colonnes(s.nom_table, s.donnees, $2)::text, s.ordre
            FROM sync_sortie s JOIN sync_parametre p ON p.id = 1 AND p.epoque = s.epoque
            WHERE s.lot_numero IS NULL AND ($3::text[] IS NULL OR s.nom_table = ANY ($3))
            ORDER BY coalesce(s.ordre, s.id), s.id LIMIT $1
            """);
        cmd.Parameters.AddWithValue(maximum);
        cmd.Parameters.Add(new NpgsqlParameter { Value = colonnesRetirees.ToArray(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)tables?.ToArray() ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneSortieSync>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneSortieSync(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6)));
        return liste;
    }

    public async Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_constituer_lot($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(lot.Numero);
        cmd.Parameters.AddWithValue(lot.Lignes.ToArray());
        cmd.Parameters.AddWithValue(lot.Octets);
        cmd.Parameters.AddWithValue(lot.Empreinte);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)lot.ColonnesRetirees?.ToArray() ?? Array.Empty<string>(), NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Text });
        return await cmd.ExecuteScalarAsync(ct) is long;
    }
}
