using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>Supervision (bandeau, alertes, réglages) et purge de la file d'envoi : script 140.</summary>
public sealed class SupervisionSyncSql(NpgsqlDataSource db) : ISupervisionSyncDepot, IPurgeSyncDepot
{
    private static readonly string[] Natures = [NaturesAlerteSync.Retard, NaturesAlerteSync.Erreur, NaturesAlerteSync.Certificat, NaturesAlerteSync.Quota];

    public async Task<EtatSupervisionSync> LireAsync(CancellationToken ct = default)
    {
        // Plus ancienne opération non accusée : première ligne hors lot (index partiel sur l'identifiant) ou première ligne du premier lot
        // non accusé ; nombre : lignes hors lot + lignes des lots non accusés
        await using var cmd = db.CreateCommand("""
            SELECT p.active, pg_is_in_recovery(),
                   least(
                       (SELECT s.survenu_le FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque ORDER BY s.id LIMIT 1),
                       (SELECT min(s.survenu_le) FROM sync_sortie s WHERE s.epoque = p.epoque
                           AND s.lot_numero = (SELECT min(l.numero) FROM sync_lot l WHERE l.epoque = p.epoque AND l.accuse_le IS NULL))),
                   (SELECT count(*) FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque)
                     + coalesce((SELECT sum(l.nb_lignes) FROM sync_lot l WHERE l.epoque = p.epoque AND l.accuse_le IS NULL), 0),
                   p.erreur_depuis_le, p.erreur_nature, p.derniere_erreur,
                   p.alerte_retard_le, p.alerte_erreur_le, p.alerte_certificat_le, p.alerte_quota_le,
                   p.bandeau_minutes, p.alerte_retard_heures, p.alerte_erreur_minutes, p.purge_active, p.purge_jours,
                   (SELECT rc.valeur::text FROM reglage_central rc WHERE rc.cle = 'integrite_centrale'), p.integrite_anomalie_signalee,
                   p.retour_arriere_le, p.retour_arriere_central, p.dernier_lot, p.epoque, p.retour_arriere_sequences_le IS NOT NULL,
                   p.retour_arriere_alerte_le
            FROM sync_parametre p WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        var alertes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        for (var i = 0; i < Natures.Length; i++)
            if (Date(r, 7 + i) is { } d) alertes[Natures[i]] = d;
        return new EtatSupervisionSync(r.GetBoolean(0), r.GetBoolean(1), new AttenteSync(Date(r, 2), Convert.ToInt64(r.GetValue(3))), Date(r, 4),
            r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6), alertes,
            new ReglagesSupervisionSync(r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetBoolean(14), r.GetInt32(15)),
            IntegriteCentrale.Lire(r.IsDBNull(16) ? null : r.GetString(16)), r.GetInt64(17),
            Date(r, 18) is { } ra && !r.IsDBNull(19)
                ? new RetourArriereSync(ra, r.GetInt64(19), r.GetInt64(20), r.GetInt32(21), r.GetBoolean(22), Date(r, 23)) : null);
    }

    public async Task NoterIntegriteSignaleeAsync(long derniereAnomalieId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_integrite_signalee($1)");
        cmd.Parameters.AddWithValue(derniereAnomalieId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterAlerteAsync(string nature, DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_alerte($1, $2)");
        cmd.Parameters.AddWithValue(nature);
        cmd.Parameters.Add(new NpgsqlParameter { Value = DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterAlerteRetourArriereAsync(DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_alerte_retour_arriere($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnregistrerReglagesAsync(ReglagesSupervisionSync reglages, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_regler_supervision($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(reglages.BandeauMinutes);
        cmd.Parameters.AddWithValue(reglages.RetardHeures);
        cmd.Parameters.AddWithValue(reglages.ErreurMinutes);
        cmd.Parameters.AddWithValue(reglages.PurgeActive);
        cmd.Parameters.AddWithValue(reglages.PurgeJours);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<EtatPurgeSync> EtatAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.purge_active, p.purge_jours, p.purge_epoque, p.purge_dernier_lot, p.derniere_purge_le, p.derniere_purge_lignes,
                   p.purge_lignes_total, p.derniere_purge_erreur,
                   (SELECT nullif(c.reltuples, -1)::bigint FROM pg_class c WHERE c.oid = 'sync_sortie'::regclass),
                   pg_total_relation_size('sync_sortie'), pg_is_in_recovery()
            FROM sync_parametre p WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        return new EtatPurgeSync(r.GetBoolean(0), r.GetInt32(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.GetInt64(3), Date(r, 4), r.GetInt64(5), r.GetInt64(6),
            r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetInt64(9), r.GetBoolean(10));
    }

    public async Task<ResultatPurgeSync> PurgerAsync(int delaiJours, int maxLots, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT lots, lignes, octets, jusqu_au_lot FROM sync_purger($1, $2)");
        cmd.Parameters.AddWithValue(delaiJours);
        cmd.Parameters.AddWithValue(maxLots);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ResultatPurgeSync(r.GetInt32(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3)) : ResultatPurgeSync.Rien;
    }

    public async Task NoterErreurAsync(string? erreur, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_purge_erreur($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)erreur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
}
