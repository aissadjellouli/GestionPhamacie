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

    /// <summary>
    /// Retours clients enregistrés pendant la période (ventes non annulées) : ils diminuent le chiffre du jour où la caisse
    /// a repris les boîtes, pas celui du jour de la vente d'origine. Une vente annulée sort entièrement du chiffre.
    /// </summary>
    private const string RetoursPeriode = """
        retour_client rc JOIN vente vr ON vr.id = rc.vente_id AND vr.statut = 'validee'
        WHERE rc.cree_le >= $1::date AND rc.cree_le < ($2::date + 1)
        """;

    /// <summary>Boîtes rendues pendant la période, par ligne de vente : quantité, part payée rendue, boîtes remises en stock.</summary>
    private const string LignesRendues = $"""
        SELECT lv.id AS ligne_vente_id, lv.produit_id, lv.lot_id, rl.quantite, rl.montant, rl.destination
        FROM retour_client_ligne rl
        JOIN retour_client rc ON rc.id = rl.retour_id
        JOIN vente vr ON vr.id = rc.vente_id AND vr.statut = 'validee'
        JOIN ligne_vente lv ON lv.id = rl.ligne_vente_id
        WHERE rc.cree_le >= $1::date AND rc.cree_le < ($2::date + 1)
        """;

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
                   COALESCE(SUM(v.montant_total) FILTER (WHERE v.statut = 'validee'), 0)
                       - (SELECT COALESCE(SUM(rc.montant_rembourse), 0) FROM {RetoursPeriode}),
                   COALESCE(SUM(v.part_organisme) FILTER (WHERE v.statut = 'validee'), 0),
                   COALESCE(SUM(v.montant_remise) FILTER (WHERE v.statut = 'validee'), 0),
                   count(*) FILTER (WHERE v.statut = 'annulee')::int,
                   (SELECT COALESCE(SUM(p.montant), 0) FROM paiement p JOIN vente v ON v.id = p.vente_id
                    WHERE p.mode = 'credit' AND v.statut = 'validee' AND {Periode}),
                   (SELECT count(*) FROM {RetoursPeriode} AND EXISTS (SELECT 1 FROM retour_client_ligne rl WHERE rl.retour_id = rc.id))::int,
                   (SELECT COALESCE(SUM(rc.montant_rembourse), 0) FROM {RetoursPeriode}),
                   (SELECT COALESCE(SUM(l.prix_unitaire * l.quantite) FILTER (WHERE COALESCE(cat.medicament, true)), 0)
                    FROM ligne_vente l JOIN vente v ON v.id = l.vente_id JOIN produit p ON p.id = l.produit_id
                    LEFT JOIN categorie_produit cat ON cat.code = p.categorie
                    WHERE v.statut = 'validee' AND {Periode}),
                   (SELECT COALESCE(SUM(l.prix_unitaire * l.quantite) FILTER (WHERE NOT COALESCE(cat.medicament, true)), 0)
                    FROM ligne_vente l JOIN vente v ON v.id = l.vente_id JOIN produit p ON p.id = l.produit_id
                    LEFT JOIN categorie_produit cat ON cat.code = p.categorie
                    WHERE v.statut = 'validee' AND {Periode}),
                   (SELECT COALESCE(SUM(l.shp * l.quantite), 0) FROM ligne_vente l JOIN vente v ON v.id = l.vente_id
                    WHERE v.statut = 'validee' AND {Periode}),
                   COALESCE(SUM(v.montant_don) FILTER (WHERE v.statut = 'validee'), 0),
                   (SELECT COALESCE(SUM(l.reduction_auto), 0) FROM ligne_vente l JOIN vente v ON v.id = l.vente_id
                    WHERE v.statut = 'validee' AND {Periode})
            FROM vente v WHERE {Periode}
            """, du, au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new SyntheseVentes(r.GetInt32(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(5), r.GetDecimal(3), r.GetInt32(4),
            r.GetInt32(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetDecimal(11), r.GetDecimal(12));
    }

    public async Task<IReadOnlyList<VentesJour>> ParJourAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT j::date, count(v.id)::int,
                   COALESCE(SUM(v.montant_total), 0)
                       - (SELECT COALESCE(SUM(rc.montant_rembourse), 0)
                          FROM retour_client rc JOIN vente vr ON vr.id = rc.vente_id AND vr.statut = 'validee'
                          WHERE rc.cree_le >= j AND rc.cree_le < j + interval '1 day')
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
            ,
            vd AS (SELECT n.produit_id, SUM(n.quantite) AS quantite, SUM(n.net) AS net FROM n GROUP BY n.produit_id),
            rt AS (SELECT x.produit_id, SUM(x.quantite) AS quantite, SUM(x.montant) AS montant FROM ({LignesRendues}) x GROUP BY x.produit_id)
            SELECT p.nom_commercial, (COALESCE(vd.quantite, 0) - COALESCE(rt.quantite, 0))::int,
                   ROUND(COALESCE(vd.net, 0) - COALESCE(rt.montant, 0), 2)
            FROM (SELECT produit_id FROM vd UNION SELECT produit_id FROM rt) t
            JOIN produit p ON p.id = t.produit_id
            LEFT JOIN vd ON vd.produit_id = t.produit_id
            LEFT JOIN rt ON rt.produit_id = t.produit_id
            ORDER BY 2 DESC, 3 DESC, p.nom_commercial
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
            ,
            -- Retours de la période : la part rendue sort du chiffre HT ; le coût ne revient que pour les boîtes remises en stock
            r AS (SELECT x.montant, p.taux_tva, CASE WHEN x.destination = 'stock' THEN x.quantite * lo.prix_achat_ht ELSE 0 END AS cout
                  FROM ({LignesRendues}) x JOIN produit p ON p.id = x.produit_id JOIN lot lo ON lo.id = x.lot_id)
            SELECT COALESCE((SELECT ROUND(SUM(net / (1 + taux_tva / 100)), 2) FROM c), 0)
                       - COALESCE((SELECT ROUND(SUM(montant / (1 + taux_tva / 100)), 2) FROM r), 0),
                   COALESCE((SELECT ROUND(SUM(cout), 2) FROM c), 0) - COALESCE((SELECT ROUND(SUM(cout), 2) FROM r), 0)
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
                   (SELECT count(*) FROM vente v WHERE v.session_caisse_id = s.id AND v.statut = 'validee' AND (v.montant_remise > 0 OR v.montant_don > 0))::int,
                   (SELECT count(*) FROM retour_client rc WHERE rc.session_caisse_id = s.id
                      AND EXISTS (SELECT 1 FROM retour_client_ligne rl WHERE rl.retour_id = rc.id))::int,
                   u.id
            FROM session_caisse s JOIN utilisateur u ON u.id = s.utilisateur_id
            WHERE s.fermee_le >= $1::date AND s.fermee_le < ($2::date + 1)
            ORDER BY s.fermee_le DESC
            """, du, au);
        var liste = new List<ClotureRapport>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ClotureRapport(r.GetString(0), r.GetString(1), r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetDateTime(3),
                r.IsDBNull(4) ? null : r.GetDecimal(4), r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetDecimal(6),
                r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt64(10)));
        return liste;
    }

    public async Task<IReadOnlyList<RetourRapport>> RetoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        // Un retour peut tenir en deux écritures de la même transaction (dette diminuée + argent rendu) : elles ont la même date
        await using var cmd = Commande("""
            SELECT v.numero, v.date_vente, uv.nom_complet, rc.cree_le, COALESCE(rc.poste, ''), ur.nom_complet,
                   COALESCE((SELECT string_agg(p.nom_commercial || ' × ' || rl.quantite, ', ' ORDER BY p.nom_commercial)
                             FROM retour_client r2
                             JOIN retour_client_ligne rl ON rl.retour_id = r2.id
                             JOIN ligne_vente lv ON lv.id = rl.ligne_vente_id
                             JOIN produit p ON p.id = lv.produit_id
                             WHERE r2.vente_id = v.id AND r2.cree_le = rc.cree_le), ''),
                   SUM(rc.montant_rembourse),
                   COALESCE(SUM(rc.montant_rembourse) FILTER (WHERE rc.mode = 'credit'), 0),
                   (rc.cree_le >= $1::date AND rc.cree_le < ($2::date + 1)),
                   uv.id, ur.id
            FROM retour_client rc
            JOIN vente v ON v.id = rc.vente_id AND v.statut = 'validee'
            JOIN utilisateur uv ON uv.id = v.utilisateur_id
            JOIN utilisateur ur ON ur.id = rc.utilisateur_id
            WHERE (rc.cree_le >= $1::date AND rc.cree_le < ($2::date + 1))
               OR (v.date_vente >= $1::date AND v.date_vente < ($2::date + 1))
            GROUP BY v.id, v.numero, v.date_vente, uv.id, uv.nom_complet, rc.cree_le, rc.poste, ur.id, ur.nom_complet
            ORDER BY rc.cree_le DESC, v.numero
            """, du, au);
        var liste = new List<RetourRapport>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new RetourRapport(r.GetString(0), r.GetDateTime(1), r.GetString(2), r.GetDateTime(3), r.GetString(4), r.GetString(5),
                r.GetString(6), r.GetDecimal(7), r.GetDecimal(8), r.GetBoolean(9), r.GetInt64(10), r.GetInt64(11)));
        return liste;
    }

    public async Task<IReadOnlyList<SortieSansVente>> SortiesAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        // Périmés, casses et dons : mouvements négatifs écrits par la sortie de stock ; le prix d'achat est fixé à l'écriture du mouvement
        await using var cmd = Commande("""
            SELECT m.cree_le, m.type, p.nom_commercial, lo.numero_lot, abs(m.quantite),
                   ROUND(abs(m.quantite) * COALESCE(m.prix_achat_ht, lo.prix_achat_ht), 2),
                   COALESCE(u.nom_complet, '—'), m.reference, u.id,
                   ROUND(abs(m.quantite) * COALESCE(m.prix_achat_ht, lo.prix_achat_ht) * p.taux_tva / 100, 2)
            FROM mouvement_stock m
            JOIN lot lo ON lo.id = m.lot_id
            JOIN produit p ON p.id = lo.produit_id
            LEFT JOIN utilisateur u ON u.id = m.utilisateur_id
            WHERE m.type IN ('perime', 'casse', 'echantillon')
              AND m.cree_le >= $1::date AND m.cree_le < ($2::date + 1)
            ORDER BY m.cree_le DESC, m.id DESC
            """, du, au);
        var liste = new List<SortieSansVente>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new SortieSansVente(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetDecimal(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8), r.GetDecimal(9)));
        return liste;
    }

    public async Task<TvaSorties> TvaSortiesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT tva_reversee_perime, tva_reversee_casse, tva_reversee_don FROM parametre_rapport WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        // Sans ligne de réglage : aucune TVA reversée (valeur par défaut du script 061)
        return await r.ReadAsync(ct) ? new TvaSorties(r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2)) : new TvaSorties(false, false, false);
    }

    public async Task ModifierTvaSortiesAsync(TvaSorties tva, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_rapport (id, tva_reversee_perime, tva_reversee_casse, tva_reversee_don) VALUES (1, $1, $2, $3)
            ON CONFLICT (id) DO UPDATE SET tva_reversee_perime = EXCLUDED.tva_reversee_perime,
                tva_reversee_casse = EXCLUDED.tva_reversee_casse, tva_reversee_don = EXCLUDED.tva_reversee_don
            """);
        cmd.Parameters.AddWithValue(tva.Perime);
        cmd.Parameters.AddWithValue(tva.Casse);
        cmd.Parameters.AddWithValue(tva.Don);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private NpgsqlCommand Commande(string sql, DateOnly du, DateOnly au)
    {
        var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        return cmd;
    }
}
