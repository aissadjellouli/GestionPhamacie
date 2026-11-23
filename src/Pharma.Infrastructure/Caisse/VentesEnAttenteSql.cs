using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Table panier_attente (026_ventes_attente.sql ; motif et produits dus : 094_motifs_reglables.sql).</summary>
public sealed class VentesEnAttenteSql(NpgsqlDataSource db) : IVentesEnAttente
{
    public async Task<IReadOnlyList<VenteEnAttente>> ListerAsync(string poste, CancellationToken ct = default)
    {
        // Motif affiché : « Autre : texte », sinon le libellé réglé (même désactivé depuis), sinon le code
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.libelle, a.lignes, a.total, u.nom_complet, a.cree_le,
                   CASE WHEN a.motif_code IS NULL THEN NULL
                        WHEN a.motif_code = 'autre' AND a.motif_texte IS NOT NULL THEN 'Autre : ' || a.motif_texte
                        ELSE COALESCE(m.libelle, a.motif_code) END,
                   a.produits_dus::text
            FROM panier_attente a JOIN utilisateur u ON u.id = a.utilisateur_id
            LEFT JOIN motif_reglable m ON m.liste = 'attente' AND m.code = a.motif_code
            WHERE a.poste = $1 ORDER BY a.cree_le
            """);
        cmd.Parameters.AddWithValue(poste);
        var liste = new List<VenteEnAttente>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new VenteEnAttente(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4), r.GetDateTime(5),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : JsonSerializer.Deserialize<List<ProduitDu>>(r.GetString(7))));
        return liste;
    }

    public async Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, int lignes, decimal total, string contenu,
        MotifAttente? motif, CancellationToken ct = default, int maxParPoste = int.MaxValue)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Verrou du poste, levé à la fin de la transaction : le compte et l'insertion ne se croisent pas avec un autre envoi du même poste
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended('panier_attente:' || $1, 0))", cx, tx))
        {
            verrou.Parameters.AddWithValue(poste);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        await using (var compte = new NpgsqlCommand("SELECT count(*) FROM panier_attente WHERE poste = $1", cx, tx))
        {
            compte.Parameters.AddWithValue(poste);
            if ((long)(await compte.ExecuteScalarAsync(ct))! >= maxParPoste)
                throw new RegleMetierException(GererVentesEnAttente.MessageTropDAttente);
        }
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO panier_attente (poste, utilisateur_id, libelle, lignes, total, contenu, motif_code, motif_texte, produits_dus)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9) RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(lignes);
        cmd.Parameters.AddWithValue(total);
        cmd.Parameters.Add(new NpgsqlParameter { Value = contenu, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif?.Code ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif?.Texte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter
        {
            Value = motif?.ProduitsDus is { Count: > 0 } dus ? JsonSerializer.Serialize(dus) : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Jsonb
        });
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<string?> ReprendreAsync(long id, string poste, CancellationToken ct = default)
    {
        // Retiré en une seule requête : deux employés ne peuvent pas reprendre le même panier
        await using var cmd = db.CreateCommand("DELETE FROM panier_attente WHERE id = $1 AND poste = $2 RETURNING contenu::text");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(poste);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<decimal?> SupprimerAsync(long id, string poste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM panier_attente WHERE id = $1 AND poste = $2 RETURNING COALESCE(total, 0)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(poste);
        return await cmd.ExecuteScalarAsync(ct) as decimal?;
    }
}
