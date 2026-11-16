using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>Réglages d'envoi, lots non accusés et accusés (scripts 126 à 128).</summary>
public sealed class EnvoiSyncSql(NpgsqlDataSource db) : IEnvoiSyncDepot
{
    public async Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.active, p.officine_id, p.epoque, p.version_contrat, p.dernier_lot, p.dernier_accuse, p.adresse_serveur, p.inscrite_le,
                   p.dernier_envoi_le, p.dernier_accuse_le, p.derniere_erreur, p.derniere_erreur_le, p.textes_libres, p.envoyer_medecins,
                   (SELECT count(*) FROM sync_sortie s WHERE s.lot_numero IS NULL AND s.epoque = p.epoque),
                   (SELECT count(*) FROM sync_lot l WHERE l.accuse_le IS NULL AND l.epoque = p.epoque),
                   pg_is_in_recovery(),
                   (SELECT count(*) FROM sync_lot l WHERE l.accuse_le IS NULL AND l.epoque < p.epoque)
            FROM sync_parametre p WHERE p.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglage de synchronisation absent (script 126).");
        return new ParametresEnvoiSync(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetGuid(1), r.GetInt32(2), r.GetString(3), r.GetInt64(4), r.GetInt64(5),
            r.IsDBNull(6) ? null : r.GetString(6), Date(r, 7), Date(r, 8), Date(r, 9), r.IsDBNull(10) ? null : r.GetString(10), Date(r, 11),
            r.GetString(12), r.GetBoolean(13), r.GetInt64(14), r.GetInt64(15), r.GetBoolean(16), r.GetInt64(17));
    }

    public async Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.epoque, l.numero, l.version_contrat, l.empreinte FROM sync_lot l JOIN sync_parametre p ON p.id = 1 AND p.epoque = l.epoque
            WHERE l.accuse_le IS NULL ORDER BY l.numero LIMIT $1
            """);
        cmd.Parameters.AddWithValue(maximum);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var res = new List<LotEnregistre>();
        while (await r.ReadAsync(ct)) res.Add(new LotEnregistre(r.GetInt32(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return res;
    }

    public async Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT epoque, numero, version_contrat, empreinte FROM sync_lot WHERE epoque = $1 AND numero BETWEEN $2 AND $3 ORDER BY numero
            """);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var res = new List<LotEnregistre>();
        while (await r.ReadAsync(ct)) res.Add(new LotEnregistre(r.GetInt32(0), r.GetInt64(1), r.GetString(2), r.GetString(3)));
        return res;
    }

    public async Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.nom_table, s.id_local, s.operation, s.survenu_le, s.donnees::text
            FROM sync_sortie s WHERE s.epoque = $1 AND s.lot_numero = $2 ORDER BY s.id
            """);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(numero);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LigneSortieSync>();
        while (await r.ReadAsync(ct))
            liste.Add(new LigneSortieSync(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5)));
        return liste;
    }

    public Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default) =>
        ExecuterAsync("SELECT sync_enregistrer_accuse($1, $2)", ct, epoque, numero);

    public async Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_noter_envoi($1)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)erreur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default) =>
        ExecuterAsync("SELECT sync_enregistrer_inscription($1)", ct, officine);

    /// <summary>Adresse, options et activation (pose ou retrait des déclencheurs) dans une seule transaction.</summary>
    public async Task ReglerAsync(string? adresse, string textesLibres, bool envoyerMedecins, bool active, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var c = new NpgsqlCommand("SELECT sync_regler_adresse($1)", cnx, tx))
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)adresse ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("SELECT sync_definir_options($1, $2)", cnx, tx))
        {
            c.Parameters.AddWithValue(textesLibres);
            c.Parameters.AddWithValue(envoyerMedecins);
            await c.ExecuteNonQueryAsync(ct);
        }
        await using (var c = new NpgsqlCommand("SELECT sync_definir_active($1)", cnx, tx))
        {
            c.Parameters.AddWithValue(active);
            await c.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private async Task ExecuterAsync(string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static DateTime? Date(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Utc);
}
