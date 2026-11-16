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
                   (SELECT count(*) FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque)
            FROM sync_parametre p WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        return new EtatSync(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
            r.GetInt32(6), r.GetInt32(7), r.GetInt64(8));
    }

    public async Task<IReadOnlyList<LigneSortieSync>> LignesEnAttenteAsync(int maximum, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.nom_table, s.id_local, s.operation, s.survenu_le, s.donnees::text
            FROM sync_sortie s JOIN sync_parametre p ON p.id = 1 AND p.epoque = s.epoque
            WHERE s.lot_numero IS NULL
            ORDER BY s.id LIMIT $1
            """);
        cmd.Parameters.AddWithValue(maximum);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneSortieSync>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneSortieSync(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5)));
        return liste;
    }

    public async Task<bool> EnregistrerLotAsync(LotConstitue lot, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_enregistrer_lot($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(lot.Numero);
        cmd.Parameters.AddWithValue(lot.Lignes.ToArray());
        cmd.Parameters.AddWithValue(lot.Octets);
        cmd.Parameters.AddWithValue(lot.Empreinte);
        return await cmd.ExecuteScalarAsync(ct) is long;
    }
}
