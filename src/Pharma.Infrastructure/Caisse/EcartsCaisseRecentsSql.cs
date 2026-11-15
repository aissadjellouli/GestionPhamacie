using Npgsql;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Écarts de caisse récents non vus par le titulaire (session_caisse, ecart_caisse_vu du script 079) : tuile de son accueil.</summary>
public sealed class EcartsCaisseRecentsSql(NpgsqlDataSource db) : IEcartsCaisseRecents
{
    public async Task<EcartsCaisseNonVus> NonVusAsync(int jours, string? texte, DemandePage page, CancellationToken ct = default)
    {
        // Employé qui a clôturé (compté) : session_caisse ne le garde pas ; la clôture avec écart non nul est inscrite au journal
        // (action « ecart_caisse », au nom de celui qui a compté, relève comprise). Sans cette trace : celui qui a ouvert la caisse.
        // Seules les sessions closes (fermee_le) : une par ligne, à la date de leur clôture ; relève : une session l'a suivie (releve_de).
        // La caisse rouverte ensuite sur le même poste est une autre session, sans écart tant qu'elle n'est pas close.
        await using var cmd = db.CreateCommand("""
            SELECT s.id, s.poste, COALESCE(c.nom_complet, u.nom_complet), s.fermee_le, s.ecart, count(*) OVER (), c.nom_complet IS NOT NULL,
                   s.ouverte_le, EXISTS (SELECT 1 FROM session_caisse n WHERE n.releve_de = s.id)
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            LEFT JOIN LATERAL (
                SELECT uc.nom_complet FROM journal_audit j JOIN utilisateur uc ON uc.id = j.utilisateur_id
                WHERE j.cree_le >= now() - make_interval(days => $1 + 1)
                  AND j.action = 'ecart_caisse' AND j.table_cible = 'session_caisse' AND j.enregistrement_id = s.id
                ORDER BY j.cree_le DESC, j.id DESC LIMIT 1) c ON true
            WHERE s.fermee_le IS NOT NULL AND s.fermee_le >= now() - make_interval(days => $1) AND s.ecart <> 0
              AND NOT EXISTS (SELECT 1 FROM ecart_caisse_vu v WHERE v.session_id = s.id)
              AND ($4::text IS NULL OR s.poste ILIKE '%' || $4 || '%' OR COALESCE(c.nom_complet, u.nom_complet) ILIKE '%' || $4 || '%')
            ORDER BY s.fermee_le DESC, s.id DESC LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.AddWithValue(jours);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        var liste = new List<EcartCaisseRecent>();
        var nombre = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            liste.Add(new EcartCaisseRecent(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetDecimal(4), r.GetBoolean(6),
                r.GetDateTime(7), r.GetBoolean(8)));
            nombre = (int)r.GetInt64(5);
        }
        return new EcartsCaisseNonVus(nombre, liste);
    }

    public async Task<int> MarquerVusAsync(long jusquA, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO ecart_caisse_vu (session_id, vu_par)
            SELECT s.id, $2 FROM session_caisse s
            WHERE s.fermee_le IS NOT NULL AND s.ecart <> 0
              AND s.fermee_le <= (SELECT r.fermee_le FROM session_caisse r WHERE r.id = $1)
            ON CONFLICT (session_id) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(jusquA);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
