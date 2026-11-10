using Npgsql;
using Pharma.Application.Rapports;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Rapports d'activité. Les jours sont ceux du fuseau du serveur PostgreSQL (celui du PC serveur de la pharmacie).
/// Seules les ventes validées comptent ; la remise d'une vente est répartie sur ses lignes au prorata.
/// </summary>
public sealed class RapportsSql(NpgsqlDataSource db) : IRapports
{
    private const string Periode = "v.date_vente >= $1::date AND v.date_vente < ($2::date + 1)";

    /// <summary>Lignes des ventes validées de la période, avec leur montant net de remise (« net »).</summary>
    private const string LignesNettes = $"""
        WITH v AS (SELECT v.id, v.montant_total FROM vente v WHERE v.statut = 'validee' AND {Periode}),
        l AS (SELECT lv.id, lv.produit_id, lv.lot_id, lv.quantite, lv.prix_unitaire * lv.quantite AS brut,
                     SUM(lv.prix_unitaire * lv.quantite) OVER (PARTITION BY lv.vente_id) AS brut_vente, v.montant_total
              FROM ligne_vente lv JOIN v ON v.id = lv.vente_id),
        n AS (SELECT l.*, CASE WHEN l.brut_vente > 0 THEN l.brut * l.montant_total / l.brut_vente ELSE 0 END AS net FROM l)
        """;

    public async Task<SyntheseVentes> SyntheseAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande($"""
            SELECT count(*) FILTER (WHERE v.statut = 'validee')::int,
                   COALESCE(SUM(v.montant_total) FILTER (WHERE v.statut = 'validee'), 0),
                   COALESCE(SUM(v.part_organisme) FILTER (WHERE v.statut = 'validee'), 0),
                   COALESCE(SUM(v.montant_remise) FILTER (WHERE v.statut = 'validee'), 0),
                   count(*) FILTER (WHERE v.statut = 'annulee')::int,
                   (SELECT COALESCE(SUM(p.montant), 0) FROM paiement p JOIN vente v ON v.id = p.vente_id
                    WHERE p.mode = 'credit' AND v.statut = 'validee' AND {Periode})
            FROM vente v WHERE {Periode}
            """, du, au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new SyntheseVentes(r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(5), r.GetDecimal(3), r.GetInt32(4));
    }

    public async Task<IReadOnlyList<VentesJour>> ParJourAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT j::date, count(v.id)::int, COALESCE(SUM(v.montant_total), 0)
            FROM generate_series($1::date, $2::date, interval '1 day') j
            LEFT JOIN vente v ON v.statut = 'validee' AND v.date_vente >= j AND v.date_vente < j + interval '1 day'
            GROUP BY j ORDER BY j
            """, du, au);
        var liste = new List<VentesJour>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new VentesJour(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetInt32(1), r.GetDecimal(2)));
        return liste;
    }

    public async Task<IReadOnlyList<ProduitVendu>> TopProduitsAsync(DateOnly du, DateOnly au, int limite, CancellationToken ct = default)
    {
        await using var cmd = Commande($"""
            {LignesNettes}
            SELECT p.nom_commercial, SUM(n.quantite)::int, ROUND(SUM(n.net), 2)
            FROM n JOIN produit p ON p.id = n.produit_id
            GROUP BY p.id, p.nom_commercial
            ORDER BY SUM(n.quantite) DESC, SUM(n.net) DESC, p.nom_commercial
            LIMIT $3
            """, du, au);
        cmd.Parameters.AddWithValue(limite);
        var liste = new List<ProduitVendu>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new ProduitVendu(r.GetString(0), r.GetInt32(1), r.GetDecimal(2)));
        return liste;
    }

    public async Task<MargeBrute> MargeAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        // Coût : prix réel des boîtes identifiées vendues sur la ligne, prix du lot pour les autres
        await using var cmd = Commande($"""
            {LignesNettes},
            c AS (SELECT n.net, p.taux_tva,
                         COALESCE(b.somme, 0) + GREATEST(n.quantite - COALESCE(b.nb, 0), 0) * lo.prix_achat_ht AS cout
                  FROM n
                  JOIN produit p ON p.id = n.produit_id
                  JOIN lot lo ON lo.id = n.lot_id
                  LEFT JOIN (SELECT ligne_vente_id, count(*) AS nb, SUM(prix_achat_ht) AS somme
                             FROM boite WHERE ligne_vente_id IS NOT NULL GROUP BY ligne_vente_id) b ON b.ligne_vente_id = n.id)
            SELECT COALESCE(ROUND(SUM(net / (1 + taux_tva / 100)), 2), 0), COALESCE(ROUND(SUM(cout), 2), 0) FROM c
            """, du, au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new MargeBrute(r.GetDecimal(0), r.GetDecimal(1));
    }

    public async Task<IReadOnlyList<ClotureRapport>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT u.nom_complet, s.poste, s.ouverte_le, s.fermee_le, s.montant_attendu, s.montant_compte, s.ecart,
                   (SELECT count(*) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'annulee')::int,
                   (SELECT count(*) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'validee' AND v.montant_remise > 0)::int
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.fermee_le >= $1::date AND s.fermee_le < ($2::date + 1)
            ORDER BY s.fermee_le DESC
            """, du, au);
        var liste = new List<ClotureRapport>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ClotureRapport(r.GetString(0), r.GetString(1), r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6),
                r.GetInt32(7), r.GetInt32(8)));
        return liste;
    }

    private NpgsqlCommand Commande(string sql, DateOnly du, DateOnly au)
    {
        var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        return cmd;
    }
}
