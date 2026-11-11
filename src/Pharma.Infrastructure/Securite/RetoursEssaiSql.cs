using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Essai;

namespace Pharma.Infrastructure.Securite;

/// <summary>Retours d'essai : lignes « retour_essai » du journal des actions.</summary>
public sealed class RetoursEssaiSql(NpgsqlDataSource db) : IRetoursEssai
{
    public async Task<IReadOnlyList<RetourEssai>> ListerAsync(long? utilisateurId, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT j.cree_le, COALESCE(u.nom_complet, '?'), COALESCE(j.details->>'ecran', 'Général'),
                   COALESCE(j.details->>'genre', 'suggestion'), COALESCE(j.details->>'texte', '')
            FROM journal_audit j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            WHERE j.action = 'retour_essai' AND ($1::bigint IS NULL OR j.utilisateur_id = $1)
            ORDER BY j.id DESC
            LIMIT $2
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)utilisateurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<RetourEssai>();
        while (await r.ReadAsync(ct))
            liste.Add(new RetourEssai(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4)));
        return liste;
    }
}
