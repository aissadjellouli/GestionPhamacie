using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Table panier_attente (026_ventes_attente.sql).</summary>
public sealed class VentesEnAttenteSql(NpgsqlDataSource db) : IVentesEnAttente
{
    public async Task<IReadOnlyList<VenteEnAttente>> ListerAsync(string poste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.libelle, a.lignes, a.total, u.nom_complet, a.cree_le
            FROM panier_attente a JOIN utilisateur u ON u.id = a.utilisateur_id
            WHERE a.poste = $1 ORDER BY a.cree_le
            """);
        cmd.Parameters.AddWithValue(poste);
        var liste = new List<VenteEnAttente>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new VenteEnAttente(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4), r.GetDateTime(5)));
        return liste;
    }

    public async Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, int lignes, decimal total, string contenu,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO panier_attente (poste, utilisateur_id, libelle, lignes, total, contenu)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(lignes);
        cmd.Parameters.AddWithValue(total);
        cmd.Parameters.Add(new NpgsqlParameter { Value = contenu, NpgsqlDbType = NpgsqlDbType.Jsonb });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<string?> ReprendreAsync(long id, string poste, CancellationToken ct = default)
    {
        // Retiré en une seule requête : deux employés ne peuvent pas reprendre le même panier
        await using var cmd = db.CreateCommand("DELETE FROM panier_attente WHERE id = $1 AND poste = $2 RETURNING contenu::text");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(poste);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<bool> SupprimerAsync(long id, string poste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM panier_attente WHERE id = $1 AND poste = $2");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(poste);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
