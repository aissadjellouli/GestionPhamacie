using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Offre;

namespace Pharma.Infrastructure.Offre;

/// <summary>Activations de l'offre (table activation_offre, script 172) et identité de la pharmacie (code de l'officine, NIF).</summary>
public sealed class ActivationsOffreSql(NpgsqlDataSource db) : IActivationsOffreDepot
{
    public async Task<IdentiteOffre> IdentiteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT officine_courante(), (SELECT nif FROM pharmacie WHERE id = 1)");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new IdentiteOffre(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task<IReadOnlyList<ActivationEnregistree>> ListerAsync(CancellationToken ct = default)
    {
        var liste = new List<ActivationEnregistree>();
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.code, a.emise_le, a.source, a.saisie_le, u.nom_complet, a.ordre_central, a.refusee_le, a.motif_refus
            FROM activation_offre a LEFT JOIN utilisateur u ON u.id = a.saisie_par
            ORDER BY a.emise_le DESC, a.id DESC
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ActivationEnregistree(r.GetInt64(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6),
                r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc), r.IsDBNull(8) ? null : r.GetString(8)));
        return liste;
    }

    public async Task<bool> AjouterAsync(string code, DateTime emiseLeUtc, string source, long? saisiePar, Guid? ordreCentral, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO activation_offre (code, emise_le, source, saisie_par, ordre_central) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (code) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(emiseLeUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)saisiePar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ordreCentral ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>Première constatation seulement : refusee_le et motif posés, puis une ligne au journal des actions (sans utilisateur).</summary>
    public async Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT activation_offre_signaler_refus($1, $2)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(motif.Length > 300 ? motif[..300] : motif);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
