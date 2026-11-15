using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;

namespace Pharma.Infrastructure.Achats;

/// <summary>Table reception_attente (069_receptions_attente.sql).</summary>
public sealed class ReceptionsEnAttenteSql(NpgsqlDataSource db) : IReceptionsEnAttente
{
    private const string Colonnes = "a.id, a.libelle, a.mode, a.lignes, a.utilisateur_id, u.nom_complet, a.poste, a.cree_le";

    private static ReceptionEnAttente Lire(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt64(4), r.GetString(5), r.GetString(6), r.GetDateTime(7));

    public async Task<IReadOnlyList<ReceptionEnAttente>> ListerAsync(CancellationToken ct = default)
    {
        // Facture d'un BL compté mise de côté alors que la facture de ce comptage est déjà saisie (écart à décider, validée, à recompter) :
        // plus rien à reprendre, elle n'est plus listée (2026-11-15). Le contenu est celui du poste (AFacturer.Id = comptage).
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes} FROM reception_attente a JOIN utilisateur u ON u.id = a.utilisateur_id
            WHERE NOT (a.mode = 'facture' AND jsonb_typeof(a.contenu -> 'AFacturer') = 'object'
                       -- Contenu malformé (Id absent ou non entier) : la facture reste listée, la liste ne tombe jamais en erreur (revue 2026-11-15 M4)
                       AND jsonb_typeof(a.contenu -> 'AFacturer' -> 'Id') = 'number'
                       AND NOT EXISTS (SELECT 1 FROM comptage_reception c
                                       WHERE c.id = CASE WHEN jsonb_typeof(a.contenu -> 'AFacturer' -> 'Id') = 'number'
                                                              AND (a.contenu -> 'AFacturer' ->> 'Id') ~ '^[0-9]{1,18}$'
                                                         THEN (a.contenu -> 'AFacturer' ->> 'Id')::bigint END
                                         AND c.statut = 'compte'))
            ORDER BY a.cree_le
            """);
        var liste = new List<ReceptionEnAttente>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<ReceptionEnAttente?> TrouverAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM reception_attente a JOIN utilisateur u ON u.id = a.utilisateur_id WHERE a.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, string mode, int lignes, string contenu,
        IReadOnlyList<long> scans, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO reception_attente (poste, utilisateur_id, libelle, mode, lignes, contenu, scans)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        cmd.Parameters.AddWithValue(poste);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(libelle);
        cmd.Parameters.AddWithValue(mode);
        cmd.Parameters.AddWithValue(lignes);
        cmd.Parameters.Add(new NpgsqlParameter { Value = contenu, NpgsqlDbType = NpgsqlDbType.Jsonb });
        cmd.Parameters.Add(new NpgsqlParameter { Value = scans.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Bigint });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<ReceptionReprise?> ReprendreAsync(long id, CancellationToken ct = default)
    {
        // Retirée en une seule requête : deux employés ne peuvent pas reprendre la même réception
        await using var cmd = db.CreateCommand("DELETE FROM reception_attente WHERE id = $1 RETURNING contenu::text, mode, utilisateur_id");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ReceptionReprise(r.GetString(0), r.GetString(1), r.GetInt64(2)) : null;
    }

    public async Task<(string Libelle, IReadOnlyList<long> Scans)?> SupprimerAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM reception_attente WHERE id = $1 RETURNING libelle, scans");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), r.GetFieldValue<long[]>(1)) : null;
    }
}
