using Npgsql;
using Pharma.Application.Surveillance;

namespace Pharma.Infrastructure.Surveillance;

/// <summary>Écarts dans le temps et risques par employé : lecture seule des ventes, clôtures, mouvements, journal et anomalies.</summary>
public sealed class RisquesSql(NpgsqlDataSource db) : IRisques
{
    // Présence : connexion, vente, ouverture de caisse ou action inscrite au journal ce jour-là
    private const string Presence = """
        SELECT DISTINCT x.utilisateur_id, x.jour, x.poste FROM (
            SELECT c.utilisateur_id, c.cree_le::date AS jour, c.poste FROM connexion_employe c
            UNION ALL SELECT v.utilisateur_id, v.date_vente::date, s.poste FROM vente v LEFT JOIN session_caisse s ON s.id = v.session_caisse_id
            UNION ALL SELECT s.utilisateur_id, s.ouverte_le::date, s.poste FROM session_caisse s
            UNION ALL SELECT a.utilisateur_id, a.cree_le::date, NULL FROM journal_audit a WHERE a.utilisateur_id IS NOT NULL
        ) x WHERE x.jour BETWEEN $1 AND $2
        """;

    public async Task<IReadOnlyList<JourEcarts>> JoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            WITH pres AS ({Presence}),
            caisse AS (
                SELECT s.fermee_le::date AS jour, sum(s.ecart) AS ecart, -sum(least(s.ecart, 0)) AS manque
                FROM session_caisse s WHERE s.fermee_le IS NOT NULL AND s.ecart IS NOT NULL AND s.fermee_le::date BETWEEN $1 AND $2 GROUP BY 1),
            stock AS (
                SELECT m.cree_le::date AS jour, sum(-m.quantite * l.prix_achat_ht) AS perte, sum(-m.quantite)::int AS boites
                FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
                WHERE m.type = 'ajustement_inventaire' AND m.quantite < 0 AND m.cree_le::date BETWEEN $1 AND $2 GROUP BY 1),
            anomalies AS (
                -- Jour de création de l'anomalie (et non son jour de rattachement : 1er du mois, date d'envoi d'un retour…)
                SELECT a.cree_le::date AS jour, count(*)::int AS n, count(*) FILTER (WHERE a.niveau = 'urgent')::int AS urgentes,
                       COALESCE(sum(a.valeur) FILTER (WHERE a.type = 'boites_fantomes'), 0) AS fantomes
                FROM alerte_surveillance a WHERE a.cree_le::date BETWEEN $1 AND $2 GROUP BY 1),
            jours AS (
                SELECT jour FROM pres UNION SELECT jour FROM caisse UNION SELECT jour FROM stock UNION SELECT jour FROM anomalies)
            SELECT j.jour, COALESCE(c.ecart, 0), COALESCE(c.manque, 0), COALESCE(s.perte, 0), COALESCE(s.boites, 0), COALESCE(a.fantomes, 0),
                   COALESCE(a.n, 0), COALESCE(a.urgentes, 0),
                   COALESCE((SELECT array_agg(DISTINCT p.utilisateur_id) FROM pres p WHERE p.jour = j.jour), ARRAY[]::bigint[]),
                   COALESCE((SELECT array_agg(DISTINCT p.poste ORDER BY p.poste) FROM pres p WHERE p.jour = j.jour AND p.poste IS NOT NULL), ARRAY[]::text[])
            FROM jours j
            LEFT JOIN caisse c ON c.jour = j.jour LEFT JOIN stock s ON s.jour = j.jour LEFT JOIN anomalies a ON a.jour = j.jour
            ORDER BY j.jour DESC
            """);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<JourEcarts>();
        while (await r.ReadAsync(ct))
            liste.Add(new JourEcarts(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4),
                r.GetDecimal(5), r.GetInt32(6), r.GetInt32(7), r.GetFieldValue<long[]>(8), r.GetFieldValue<string[]>(9)));
        return liste;
    }

    public async Task<IReadOnlyList<ActiviteEmploye>> EmployesAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            WITH pres AS ({Presence}),
            journal AS (SELECT j.* FROM journal_audit j WHERE j.cree_le::date BETWEEN $1 AND $2),
            mvt AS (SELECT m.* FROM mouvement_stock m WHERE m.cree_le::date BETWEEN $1 AND $2 AND m.quantite < 0)
            SELECT u.id, u.nom_complet, u.actif,
                   COALESCE((SELECT -sum(least(s.ecart, 0)) FROM session_caisse s WHERE s.utilisateur_id = u.id AND s.fermee_le::date BETWEEN $1 AND $2), 0),
                   (SELECT count(*) FROM session_caisse s WHERE s.utilisateur_id = u.id AND s.fermee_le::date BETWEEN $1 AND $2)::int,
                   (SELECT count(*) FROM alerte_surveillance a WHERE a.utilisateur_id = u.id AND a.cree_le::date BETWEEN $1 AND $2)::int,
                   (SELECT count(*) FROM alerte_surveillance a WHERE a.utilisateur_id = u.id AND a.cree_le::date BETWEEN $1 AND $2 AND a.niveau = 'urgent')::int,
                   (SELECT count(*) FROM journal j WHERE j.utilisateur_id = u.id AND j.action = 'vente_annulee')::int,
                   (SELECT count(*) FROM journal j WHERE j.utilisateur_id = u.id AND j.action = 'retour_client')::int,
                   (SELECT count(*) FROM vente v WHERE v.utilisateur_id = u.id AND v.statut = 'validee' AND v.remise_pct > 0
                      AND v.date_vente::date BETWEEN $1 AND $2)::int,
                   COALESCE((SELECT sum(c.montant) FROM credit_mouvement c WHERE c.utilisateur_id = u.id AND c.type = 'vente_credit'
                      AND c.cree_le::date BETWEEN $1 AND $2), 0),
                   (SELECT count(*) FROM journal j WHERE j.utilisateur_id = u.id AND j.action = 'autorisation_code')::int,
                   (SELECT count(*) FROM journal j WHERE j.autorise_par = u.id AND j.action = 'autorisation_code')::int,
                   COALESCE((SELECT sum(-m.quantite) FROM mvt m WHERE m.utilisateur_id = u.id AND m.type = 'casse'), 0)::int,
                   COALESCE((SELECT sum(-m.quantite) FROM mvt m WHERE m.utilisateur_id = u.id AND m.type = 'echantillon'), 0)::int,
                   COALESCE((SELECT sum(-m.quantite) FROM mvt m WHERE m.utilisateur_id = u.id AND m.type = 'ajustement_inventaire'), 0)::int
            FROM utilisateur u
            WHERE u.actif OR EXISTS (SELECT 1 FROM pres p WHERE p.utilisateur_id = u.id)
            ORDER BY u.nom_complet
            """);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ActiviteEmploye>();
        while (await r.ReadAsync(ct))
            liste.Add(new ActiviteEmploye(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetDecimal(3), r.GetInt32(4), r.GetInt32(5), r.GetInt32(6),
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetDecimal(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13), r.GetInt32(14),
                r.GetInt32(15)));
        return liste;
    }
}
