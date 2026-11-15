using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Récapitulatif de la caisse ouverte (sans espèces) et table comptage_intermediaire (070_comptage_intermediaire.sql).</summary>
public sealed class EtatIntermediaireSql(NpgsqlDataSource db) : IEtatIntermediaireCaisse
{
    public async Task<ResumeCaisse> ResumeAsync(long sessionId, CancellationToken ct = default)
    {
        // Montants par mode nets des remboursements (annulations, retours) de la session ; jamais les espèces ni le total
        await using var cmd = db.CreateCommand("""
            WITH p AS (SELECT mode, SUM(montant) AS m FROM paiement WHERE session_caisse_id = $1 GROUP BY mode),
                 r AS (SELECT mode, SUM(montant_rembourse) AS m FROM (
                         SELECT mode, montant_rembourse FROM annulation_vente WHERE session_caisse_id = $1
                         UNION ALL SELECT mode, montant_rembourse FROM retour_client WHERE session_caisse_id = $1) x GROUP BY mode)
            SELECT (SELECT count(*) FROM vente WHERE session_caisse_id = $1 AND statut = 'validee')::int,
                   COALESCE((SELECT m FROM p WHERE mode = 'carte'), 0) - COALESCE((SELECT m FROM r WHERE mode = 'carte'), 0),
                   COALESCE((SELECT m FROM p WHERE mode = 'cheque'), 0) - COALESCE((SELECT m FROM r WHERE mode = 'cheque'), 0),
                   COALESCE((SELECT m FROM p WHERE mode = 'credit'), 0) - COALESCE((SELECT m FROM r WHERE mode = 'credit'), 0),
                   COALESCE((SELECT SUM(part_organisme) FROM vente WHERE session_caisse_id = $1 AND statut = 'validee'), 0),
                   -- Avoirs des patients utilisés en paiement (script 106) : jamais des espèces
                   COALESCE((SELECT m FROM p WHERE mode = 'avoir'), 0)
            """);
        cmd.Parameters.AddWithValue(sessionId);
        int nombre; decimal carte, cheque, credit, tiers, avoir;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (nombre, carte, cheque, credit, tiers, avoir) = (r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5));
        }

        await using var parEmploye = db.CreateCommand("""
            SELECT u.nom_complet, count(*)::int FROM vente v JOIN utilisateur u ON u.id = v.utilisateur_id
            WHERE v.session_caisse_id = $1 AND v.statut = 'validee' GROUP BY u.nom_complet ORDER BY 2 DESC, 1
            """);
        parEmploye.Parameters.AddWithValue(sessionId);
        var employes = new List<(string, int)>();
        await using (var r = await parEmploye.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) employes.Add((r.GetString(0), r.GetInt32(1)));
        return new ResumeCaisse(nombre, carte, cheque, credit, tiers, employes, avoir);
    }

    public async Task<IReadOnlyList<ComptageIntermediaire>> ComptagesAsync(long sessionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.cree_le, u.nom_complet, c.compte, c.attendu, c.ecart
            FROM comptage_intermediaire c JOIN utilisateur u ON u.id = c.utilisateur_id
            WHERE c.session_caisse_id = $1 ORDER BY c.cree_le
            """);
        cmd.Parameters.AddWithValue(sessionId);
        var liste = new List<ComptageIntermediaire>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ComptageIntermediaire(r.GetDateTime(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)));
        return liste;
    }

    public async Task EnregistrerAsync(long sessionId, long utilisateurId, Comptage comptage, decimal attendu, decimal ecart, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO comptage_intermediaire (session_caisse_id, utilisateur_id, compte, attendu, ecart, comptage)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        cmd.Parameters.AddWithValue(sessionId);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(comptage.Total);
        cmd.Parameters.AddWithValue(attendu);
        cmd.Parameters.AddWithValue(ecart);
        cmd.Parameters.Add(new NpgsqlParameter { Value = System.Text.Json.JsonSerializer.Serialize(comptage.Lignes), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
