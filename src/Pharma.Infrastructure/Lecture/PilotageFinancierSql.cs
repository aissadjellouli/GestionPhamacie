using Npgsql;
using Pharma.Application.Commun;
using Pharma.Application.Pilotage;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Tableau de bord financier (lot 11) : lectures seules, jours du fuseau du serveur PostgreSQL (comme les rapports).
/// La marge par produit ou laboratoire reprend exactement le calcul de la marge des rapports (RapportsSql) ; la TVA par taux
/// est lue dans le journal fiscal (journal_vente_recap_tva).
/// </summary>
public sealed class PilotageFinancierSql(NpgsqlDataSource db) : IPilotageFinancier
{
    private NpgsqlCommand Commande(string sql, params object?[] valeurs)
    {
        var cmd = db.CreateCommand(sql);
        foreach (var v in valeurs) cmd.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
        return cmd;
    }

    private static int? Seuil(int? jours) => jours is null ? null : Math.Clamp(jours.Value, 1, 3650);

    public async Task<RepartitionEncaissement> RepartitionAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande($"""
            SELECT COALESCE((SELECT sum(p.montant) FROM paiement p JOIN vente v ON v.id = p.vente_id
                             WHERE p.mode IN ('especes', 'carte', 'cheque', 'avoir') AND v.statut = 'validee' AND {RapportsSql.Periode}), 0),
                   COALESCE((SELECT sum(p.montant) FROM paiement p JOIN vente v ON v.id = p.vente_id
                             WHERE p.mode = 'credit' AND v.statut = 'validee' AND {RapportsSql.Periode}), 0),
                   COALESCE(sum(v.part_organisme) FILTER (WHERE o.code IN ('CNAS', 'CASNOS')), 0),
                   COALESCE(sum(v.part_organisme) FILTER (WHERE o.code = 'MDN'), 0),
                   COALESCE(sum(v.part_organisme) FILTER (WHERE o.code IS NULL OR o.code NOT IN ('CNAS', 'CASNOS', 'MDN')), 0)
            FROM vente v LEFT JOIN organisme o ON o.id = v.organisme_id
            WHERE v.statut = 'validee' AND v.part_organisme > 0 AND {RapportsSql.Periode}
            """, du, au);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new RepartitionEncaissement(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4));
    }

    public async Task<IReadOnlyList<CreanceBordereau>> BordereauxNonReglesAsync(CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT id, COALESCE(numero, ''), organisme, canal, statut, date_depot, attendu, rejete, verse, reste
            FROM v_bordereau WHERE reste > 0 ORDER BY date_depot NULLS LAST, id
            """);
        var liste = new List<CreanceBordereau>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new CreanceBordereau(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : DateOnly.FromDateTime(r.GetDateTime(5)), r.GetDecimal(6), r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), null));
        return liste;
    }

    public async Task<decimal> PartOrganismesHorsBordereauAsync(CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT COALESCE(sum(v.part_organisme), 0) FROM vente v
            WHERE v.statut = 'validee' AND v.part_organisme > 0 AND v.chifa_exclue_le IS NULL
              AND NOT EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.vente_id = v.id)
            """);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<(decimal Total, int Patients)> CreditsPatientsAsync(CancellationToken ct = default)
    {
        await using var cmd = Commande("SELECT COALESCE(sum(solde_credit), 0), count(*)::int FROM patient WHERE solde_credit > 0");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return (r.GetDecimal(0), r.GetInt32(1));
    }

    public async Task<IReadOnlyList<(DateOnly DateVente, decimal Reste)>> RestesCreditAsync(CancellationToken ct = default)
    {
        await using var cmd = Commande("SELECT date_vente::date, reste FROM v_vente_credit WHERE reste > 0");
        var liste = new List<(DateOnly, decimal)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add((DateOnly.FromDateTime(r.GetDateTime(0)), r.GetDecimal(1)));
        return liste;
    }

    /// <summary>Produits dormants : même règle que l'alerte « dormant » (v_etat_stock) : lots non périmés et non bloqués.</summary>
    private const string Dormants = """
        WITH prm AS (SELECT COALESCE($1::int, (SELECT jours_dormant FROM parametre_alerte WHERE id = 1), 90) AS j),
        s AS (SELECT produit_id, sum(quantite_disponible)::int AS q, ROUND(sum(quantite_disponible * COALESCE(prix_achat_ht, 0)), 2) AS val
              FROM lot WHERE quantite_disponible > 0 AND (date_peremption IS NULL OR date_peremption > current_date) AND NOT bloque
              GROUP BY produit_id),
        d AS (SELECT lv.produit_id, max(v.date_vente) AS dv FROM ligne_vente lv JOIN vente v ON v.id = lv.vente_id
              WHERE v.statut = 'validee' AND lv.produit_id IS NOT NULL GROUP BY lv.produit_id),
        x AS (SELECT p.id, p.nom_commercial || COALESCE(' ' || NULLIF(p.dosage, ''), '') AS nom, la.nom AS labo, s.q, s.val, d.dv,
                     CASE WHEN d.dv IS NULL THEN NULL ELSE (current_date - d.dv::date) END AS jours
              FROM s JOIN produit p ON p.id = s.produit_id AND p.actif
              LEFT JOIN laboratoire la ON la.id = p.laboratoire_id
              LEFT JOIN d ON d.produit_id = p.id
              CROSS JOIN prm
              WHERE d.dv IS NULL OR d.dv < now() - make_interval(days => prm.j))
        """;

    public async Task<StockPilotage> StockAsync(int? seuilDormantJours, CancellationToken ct = default)
    {
        await using var cmd = Commande($"""
            {Dormants}
            SELECT (SELECT COALESCE(ROUND(sum(quantite_disponible * COALESCE(prix_achat_ht, 0)), 2), 0) FROM lot WHERE quantite_disponible > 0),
                   (SELECT count(DISTINCT produit_id)::int FROM lot WHERE quantite_disponible > 0),
                   (SELECT j FROM prm),
                   (SELECT count(*)::int FROM x),
                   (SELECT COALESCE(sum(val), 0) FROM x),
                   (SELECT count(*)::int FROM v_etat_stock WHERE statut = 'rupture'),
                   (SELECT count(DISTINCT produit_id)::int FROM rupture_grossiste WHERE fin IS NULL)
            """, Seuil(seuilDormantJours));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new StockPilotage(r.GetDecimal(0), r.GetInt32(1), 0, 0, r.GetInt32(2), r.GetInt32(3), r.GetDecimal(4), r.GetInt32(5), r.GetInt32(6));
    }

    public async Task<Page<ProduitDormant>> DormantsAsync(int? seuilDormantJours, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        var filtre = string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim();
        await using var cmd = Commande($"""
            {Dormants},
            f AS (SELECT * FROM x WHERE $2::text IS NULL OR nom ILIKE '%' || $2 || '%' OR labo ILIKE '%' || $2 || '%')
            SELECT id, nom, labo, q, val, dv, jours, (SELECT count(*)::int FROM f)
            FROM f ORDER BY val DESC, nom LIMIT $3 OFFSET $4
            """, Seuil(seuilDormantJours), filtre, page.Taille, page.Decalage);
        var liste = new List<ProduitDormant>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                liste.Add(new ProduitDormant(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetInt32(3), r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetDateTime(5), r.IsDBNull(6) ? null : r.GetInt32(6)));
                total = r.GetInt32(7);
            }
        return new Page<ProduitDormant>(liste, total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<AchatsGrossiste>> AchatsParGrossisteAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT f.id, f.raison_sociale, count(*)::int, COALESCE(sum(r.montant_ht), 0), COALESCE(sum(r.montant_ttc), 0)
            FROM reception r JOIN fournisseur f ON f.id = r.fournisseur_id
            WHERE r.date_reception >= $1::date AND r.date_reception < ($2::date + 1)
            GROUP BY f.id, f.raison_sociale ORDER BY 5 DESC, 2
            """, du, au);
        var liste = new List<AchatsGrossiste>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new AchatsGrossiste(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetDecimal(4)));
        return liste;
    }

    public async Task<Page<MargeRegroupee>> MargesAsync(DateOnly du, DateOnly au, RegroupementMarge regroupement, string? recherche, DemandePage page,
        CancellationToken ct = default)
    {
        var (cleId, cle) = regroupement == RegroupementMarge.Laboratoire
            ? ("COALESCE(p.laboratoire_id, 0)", "COALESCE(la.nom, 'Laboratoire non renseigné')")
            : ("p.id", "p.nom_commercial || COALESCE(' ' || NULLIF(p.dosage, ''), '')");
        var filtre = string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim();
        // Même coût que la marge des rapports : prix réel des boîtes identifiées, sinon prix du lot ; retours de la période déduits
        await using var cmd = Commande($"""
            {RapportsSql.LignesNettes},
            c AS (SELECT n.produit_id, n.quantite, n.net / (1 + COALESCE(p.taux_tva, n.taux_tva_ligne) / 100) AS ht,
                         COALESCE(b.somme, 0) + GREATEST(n.quantite - COALESCE(b.nb, 0), 0) * COALESCE(lo.prix_achat_ht, 0) AS cout
                  FROM n
                  JOIN produit p ON p.id = n.produit_id
                  LEFT JOIN lot lo ON lo.id = n.lot_id
                  LEFT JOIN (SELECT ligne_vente_id, count(*) AS nb, SUM(prix_achat_ht) AS somme
                             FROM boite WHERE ligne_vente_id IS NOT NULL GROUP BY ligne_vente_id) b ON b.ligne_vente_id = n.id),
            r AS (SELECT x.produit_id, -x.quantite AS quantite, -(x.montant / (1 + p.taux_tva / 100)) AS ht,
                         -(CASE WHEN x.destination = 'stock' THEN x.quantite * lo.prix_achat_ht ELSE 0 END) AS cout
                  FROM ({RapportsSql.LignesRendues}) x JOIN produit p ON p.id = x.produit_id JOIN lot lo ON lo.id = x.lot_id),
            t AS (SELECT * FROM c UNION ALL SELECT * FROM r),
            g AS (SELECT {cleId} AS id, {cle} AS cle, sum(t.quantite)::int AS q, ROUND(sum(t.ht), 2) AS ht, ROUND(sum(t.cout), 2) AS cout
                  FROM t JOIN produit p ON p.id = t.produit_id LEFT JOIN laboratoire la ON la.id = p.laboratoire_id
                  GROUP BY 1, 2),
            f AS (SELECT * FROM g WHERE $3::text IS NULL OR cle ILIKE '%' || $3 || '%')
            SELECT cle, q, ht, cout, (SELECT count(*)::int FROM f) FROM f
            ORDER BY ht - cout DESC, cle LIMIT $4 OFFSET $5
            """, du, au, filtre, page.Taille, page.Decalage);
        var liste = new List<MargeRegroupee>();
        var total = 0;
        await using (var rd = await cmd.ExecuteReaderAsync(ct))
            while (await rd.ReadAsync(ct))
            {
                liste.Add(new MargeRegroupee(rd.GetString(0), rd.GetInt32(1), rd.GetDecimal(2), rd.GetDecimal(3)));
                total = rd.GetInt32(4);
            }
        return new Page<MargeRegroupee>(liste, total, page.Page, page.Taille);
    }

    public async Task<RejetsChifa> RejetsAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        const string Deposees = """
            SELECT bv.* FROM bordereau_vente bv JOIN bordereau b ON b.id = bv.bordereau_id
            WHERE b.date_envoi >= $1::date AND b.date_envoi <= $2::date
            """;
        int ventes;
        decimal montant;
        await using (var cmd = Commande($"SELECT count(*)::int, COALESCE(sum(part_organisme), 0) FROM ({Deposees}) d", du, au))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            (ventes, montant) = (r.GetInt32(0), r.GetDecimal(1));
        }
        var motifs = new List<RejetsMotif>();
        await using (var cmd = Commande($"""
            SELECT d.rejet_motif_code, COALESCE(m.libelle, d.rejet_motif_code), count(*)::int, COALESCE(sum(d.montant_rejete), 0)
            FROM ({Deposees}) d
            LEFT JOIN motif_reglable m ON m.liste = 'rejet_chifa' AND m.code = d.rejet_motif_code
            WHERE d.rejete_le IS NOT NULL
            GROUP BY 1, 2 ORDER BY 3 DESC, 2
            """, du, au))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) motifs.Add(new RejetsMotif(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetDecimal(3)));
        return new RejetsChifa(ventes, montant, motifs);
    }

    public async Task<IReadOnlyList<TvaParTaux>> TvaAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await using var cmd = Commande("""
            SELECT taux, montant_ttc, montant_ht, montant_tva, evenements FROM journal_vente_recap_tva($1::date, $2::date) ORDER BY taux
            """, du, au);
        var liste = new List<TvaParTaux>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new TvaParTaux(r.GetDecimal(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt64(4)));
        return liste;
    }
}
