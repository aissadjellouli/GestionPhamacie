using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;
using Pharma.Infrastructure.Caisse;

namespace Pharma.Infrastructure.Chifa;

/// <summary>
/// Bordereaux Chifa / MDN (database/migration/110_bordereaux_chifa.sql) : bordereau, bordereau_vente (factures et rejets),
/// versement_bordereau ; montants lus dans v_bordereau, ventes à déposer dans v_vente_chifa_a_deposer.
/// vente.bordereau_id suit le bordereau actif de la vente (règle d'annulation existante).
/// </summary>
public sealed class BordereauxSql(NpgsqlDataSource db) : IBordereaux
{
    private const string Colonnes = """
        b.id, b.organisme_id, b.organisme, b.canal, b.centre_payeur_id, cp.code || ' · ' || cp.nom, b.numero, b.periode_debut, b.periode_fin,
        b.date_depot, b.statut, b.montant_declare, b.ventes, b.attendu, b.rejete, b.rejets_a_traiter, b.verse, b.reste, x.cree_le, u.nom_complet,
        (b.statut IN ('depose', 'paye_partiel') AND b.reste > 0
         AND b.date_depot < CURRENT_DATE - COALESCE((SELECT pc.chifa_delai_paiement_jours FROM parametre_caisse pc WHERE pc.id = 1), 45))
        FROM v_bordereau b
        JOIN bordereau x ON x.id = b.id
        LEFT JOIN centre_payeur cp ON cp.id = b.centre_payeur_id
        LEFT JOIN utilisateur u ON u.id = x.cree_par
        """;

    private const string FiltreListe = """
        ($1::bigint IS NULL OR b.organisme_id = $1)
        AND ($2::text IS NULL OR ($2 = 'impayes' AND b.statut IN ('depose', 'paye_partiel', 'paye')) OR b.statut = $2)
        AND ($3::text IS NULL OR b.numero ILIKE '%' || $3 || '%'
             OR EXISTS (SELECT 1 FROM bordereau_vente bv JOIN vente v ON v.id = bv.vente_id
                        WHERE bv.bordereau_id = b.id AND (v.numero ILIKE '%' || $3 || '%' OR COALESCE(v.chifa_facture, '') ILIKE '%' || $3 || '%')))
        """;

    public async Task<Page<Bordereau>> ListerAsync(FiltreBordereaux filtre, DemandePage page, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.OrganismeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Etat ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM v_bordereau b WHERE {FiltreListe}", cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"""
            SELECT {Colonnes} WHERE {FiltreListe}
            ORDER BY (b.statut = 'preparation') DESC, COALESCE(b.date_depot, b.periode_fin) DESC, b.id DESC LIMIT $4 OFFSET $5
            """, cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<Bordereau>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return new Page<Bordereau>(liste, total, page.Page, page.Taille);
    }

    public async Task<Bordereau?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE b.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<IReadOnlyList<LigneBordereau>> LignesAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT v.id, v.numero, v.date_vente, v.patient_id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), v.chifa_facture,
                   po.part_organisme_retenue, bv.ajoute_le, bv.rejete_le,
                   CASE WHEN bv.rejet_motif_code IS NULL THEN NULL
                        WHEN bv.rejet_motif_code = 'autre' AND bv.rejet_texte IS NOT NULL THEN 'Autre : ' || bv.rejet_texte
                        ELSE COALESCE((SELECT mr.libelle FROM motif_reglable mr WHERE mr.liste = 'rejet_chifa' AND mr.code = bv.rejet_motif_code),
                                      bv.rejet_motif_code) END,
                   bv.montant_rejete, bv.suite, bv.suite_le, su.nom_complet
            FROM bordereau_vente bv
            JOIN vente v ON v.id = bv.vente_id
            JOIN v_vente_part_organisme po ON po.vente_id = v.id
            LEFT JOIN patient p ON p.id = v.patient_id
            LEFT JOIN utilisateur su ON su.id = bv.suite_par
            WHERE bv.bordereau_id = $1
            ORDER BY v.date_vente, v.id
            """);
        cmd.Parameters.AddWithValue(id);
        var liste = new List<LigneBordereau>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new LigneBordereau(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetDecimal(6), r.GetDateTime(7),
                r.IsDBNull(8) ? null : r.GetDateTime(8), r.IsDBNull(9) ? null : r.GetString(9), r.GetDecimal(10),
                r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetDateTime(12), r.IsDBNull(13) ? null : r.GetString(13)));
        return liste;
    }

    public async Task<IReadOnlyList<VersementBordereau>> VersementsAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT m.id, m.date_mouvement, c.nom, m.reference, vb.montant
            FROM versement_bordereau vb
            JOIN mouvement_tresorerie m ON m.id = vb.mouvement_id
            JOIN compte_tresorerie c ON c.id = m.compte_id
            WHERE vb.bordereau_id = $1 ORDER BY m.date_mouvement, m.id
            """);
        cmd.Parameters.AddWithValue(id);
        var liste = new List<VersementBordereau>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new VersementBordereau(r.GetInt64(0), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDecimal(4)));
        return liste;
    }

    public async Task<IReadOnlyList<HistoriqueBordereau>> HistoriqueAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT j.cree_le, COALESCE(u.nom_complet, '—'), j.action, j.details::text
            FROM journal_audit j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            WHERE j.table_cible = 'bordereau' AND j.enregistrement_id = $1
            UNION ALL
            SELECT j.cree_le, COALESCE(u.nom_complet, '—'), 'versement_bordereau', j.details::text
            FROM versement_bordereau vb JOIN journal_audit j ON j.table_cible = 'mouvement_tresorerie' AND j.enregistrement_id = vb.mouvement_id
                 AND j.action = 'mouvement_tresorerie'
            LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            WHERE vb.bordereau_id = $1
            ORDER BY 1 DESC LIMIT 200
            """);
        cmd.Parameters.AddWithValue(id);
        var liste = new List<HistoriqueBordereau>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new HistoriqueBordereau(r.GetDateTime(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return liste;
    }

    // Recherche : n° de vente, patient, n° de facture Chifa ; centre 0 = sans centre ; $5 : en retard seulement
    private const string FiltreADeposer = """
        ($1::bigint IS NULL OR a.organisme_id = $1)
        AND ($2::bigint IS NULL OR ($2 = 0 AND a.centre_payeur_id IS NULL) OR a.centre_payeur_id = $2 OR ($5::boolean AND a.centre_payeur_id IS NULL))
        AND ($3::text IS NULL OR a.numero ILIKE '%' || $3 || '%' OR COALESCE(a.chifa_facture, '') ILIKE '%' || $3 || '%'
             OR trim(p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $3 || '%')
        AND (NOT $4::boolean OR a.date_vente < now() - make_interval(days => COALESCE((SELECT pc.chifa_delai_depot_jours FROM parametre_caisse pc WHERE pc.id = 1), 7)))
        """;

    private static void ParametresADeposer(NpgsqlCommand c, FiltreVentesADeposer f)
    {
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.OrganismeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.CentrePayeurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        c.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        c.Parameters.Add(new NpgsqlParameter { Value = f.RetardSeulement, NpgsqlDbType = NpgsqlDbType.Boolean });
        c.Parameters.Add(new NpgsqlParameter { Value = f.AvecSansCentre, NpgsqlDbType = NpgsqlDbType.Boolean });
    }

    public async Task<Page<VenteADeposer>> VentesADeposerAsync(FiltreVentesADeposer filtre, DemandePage page, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM v_vente_chifa_a_deposer a LEFT JOIN patient p ON p.id = a.patient_id WHERE {FiltreADeposer}", cx))
        {
            ParametresADeposer(compte, filtre);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"""
            SELECT a.vente_id, a.numero, a.date_vente, a.organisme_id, o.code, a.canal, a.centre_payeur_id, cp.code || ' · ' || cp.nom,
                   a.patient_id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), a.chifa_facture, a.part_organisme, a.redeposee,
                   a.date_vente < now() - make_interval(days => COALESCE((SELECT pc.chifa_delai_depot_jours FROM parametre_caisse pc WHERE pc.id = 1), 7))
            FROM v_vente_chifa_a_deposer a
            JOIN organisme o ON o.id = a.organisme_id
            LEFT JOIN centre_payeur cp ON cp.id = a.centre_payeur_id
            LEFT JOIN patient p ON p.id = a.patient_id
            WHERE {FiltreADeposer}
            ORDER BY a.date_vente, a.vente_id LIMIT $6 OFFSET $7
            """, cx);
        ParametresADeposer(cmd, filtre);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<VenteADeposer>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new VenteADeposer(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetInt64(3), r.GetString(4), r.GetString(5),
                r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetInt64(8),
                r.IsDBNull(9) ? null : r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10), r.GetDecimal(11), r.GetBoolean(12), r.GetBoolean(13)));
        return new Page<VenteADeposer>(liste, total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<long>> IdsVentesADeposerAsync(FiltreVentesADeposer filtre, int max, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT a.vente_id FROM v_vente_chifa_a_deposer a LEFT JOIN patient p ON p.id = a.patient_id
            WHERE {FiltreADeposer} ORDER BY a.date_vente, a.vente_id LIMIT $6
            """);
        ParametresADeposer(cmd, filtre);
        cmd.Parameters.AddWithValue(max + 1);
        var liste = new List<long>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(r.GetInt64(0));
        return liste;
    }

    public async Task<VenteRattachable?> VenteAsync(long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT v.id, v.numero, v.canal_organisme, v.statut, v.organisme_id, v.centre_payeur_id, v.chifa_exclue_le IS NOT NULL,
                   v.bordereau_id IS NOT NULL OR EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.vente_id = v.id AND bv.suite IS NULL),
                   v.chifa_facture,
                   EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.vente_id = v.id AND bv.suite IN ('perte', 'patient')),
                   COALESCE(po.part_organisme_retenue, 0), v.patient_id
            FROM vente v LEFT JOIN v_vente_part_organisme po ON po.vente_id = v.id
            WHERE v.id = $1
            """);
        cmd.Parameters.AddWithValue(venteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new VenteRattachable(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4),
            r.IsDBNull(5) ? null : r.GetInt64(5), r.GetBoolean(6), r.GetBoolean(7), r.IsDBNull(8) ? null : r.GetString(8), r.GetBoolean(9),
            r.GetDecimal(10), r.IsDBNull(11) ? null : r.GetInt64(11));
    }

    public async Task<long?> VenteParFactureAsync(string factureChifa, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id FROM vente WHERE chifa_facture = $1 AND statut = 'validee' ORDER BY id DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(factureChifa);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<CompteursBordereaux> CompteursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH p AS (SELECT COALESCE((SELECT chifa_delai_depot_jours FROM parametre_caisse WHERE id = 1), 7) AS depot,
                              COALESCE((SELECT chifa_delai_paiement_jours FROM parametre_caisse WHERE id = 1), 45) AS paiement)
            SELECT (SELECT count(*) FROM v_vente_chifa_a_deposer)::int,
                   (SELECT count(*) FROM v_vente_chifa_a_deposer a, p WHERE a.date_vente < now() - make_interval(days => p.depot))::int,
                   COALESCE((SELECT sum(part_organisme) FROM v_vente_chifa_a_deposer), 0),
                   (SELECT count(*) FROM v_bordereau b, p WHERE b.statut IN ('depose', 'paye_partiel') AND b.reste > 0
                        AND b.date_depot < CURRENT_DATE - p.paiement)::int,
                   COALESCE((SELECT sum(b.reste) FROM v_bordereau b, p WHERE b.statut IN ('depose', 'paye_partiel') AND b.reste > 0
                        AND b.date_depot < CURRENT_DATE - p.paiement), 0),
                   (SELECT count(*) FROM bordereau_vente WHERE rejete_le IS NOT NULL AND suite IS NULL)::int
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new CompteursBordereaux(r.GetInt32(0), r.GetInt32(1), r.GetDecimal(2), r.GetInt32(3), r.GetDecimal(4), r.GetInt32(5));
    }

    public async Task<long> CreerAsync(BordereauAEcrire b, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO bordereau (organisme_id, canal, centre_payeur_id, numero, periode_debut, periode_fin, montant_total, montant_declare, statut, cree_par)
            VALUES ($1, $2, $3, $4, $5, $6, 0, $7, 'preparation', $8) RETURNING id
            """);
        cmd.Parameters.AddWithValue(b.OrganismeId);
        cmd.Parameters.AddWithValue(b.Canal);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)b.CentrePayeurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(b.Numero);
        cmd.Parameters.AddWithValue(b.PeriodeDebut);
        cmd.Parameters.AddWithValue(b.PeriodeFin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)b.MontantDeclare ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.AddWithValue(utilisateurId);
        try
        {
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le bordereau n° {b.Numero} existe déjà pour cet organisme.", "numero");
        }
    }

    public async Task<bool> ModifierAsync(long id, string numero, DateOnly debut, DateOnly fin, decimal? declare, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE bordereau SET numero = $2, periode_debut = $3, periode_fin = $4, montant_declare = $5 WHERE id = $1 AND statut = 'preparation'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(debut);
        cmd.Parameters.AddWithValue(fin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)declare ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        try
        {
            return await cmd.ExecuteNonQueryAsync(ct) == 1;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le bordereau n° {numero} existe déjà pour cet organisme.", "numero");
        }
    }

    public async Task<int> AjouterVentesAsync(long bordereauId, IReadOnlyList<long> ventes, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long? centre;
        await using (var verrou = new NpgsqlCommand("SELECT centre_payeur_id FROM bordereau WHERE id = $1 AND statut = 'preparation' FOR UPDATE", cx, tx))
        {
            verrou.Parameters.AddWithValue(bordereauId);
            await using var r = await verrou.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return 0;
            centre = r.IsDBNull(0) ? null : r.GetInt64(0);
        }
        var n = 0;
        foreach (var venteId in ventes)
        {
            // Vente encore libre : rattachée, son centre vide prend celui du bordereau
            await using (var maj = new NpgsqlCommand("""
                UPDATE vente SET bordereau_id = $1, centre_payeur_id = COALESCE(centre_payeur_id, $3)
                WHERE id = $2 AND bordereau_id IS NULL AND statut = 'validee'
                """, cx, tx))
            {
                maj.Parameters.AddWithValue(bordereauId);
                maj.Parameters.AddWithValue(venteId);
                maj.Parameters.Add(new NpgsqlParameter { Value = (object?)centre ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
                if (await maj.ExecuteNonQueryAsync(ct) != 1) continue;
            }
            await using (var ins = new NpgsqlCommand("""
                INSERT INTO bordereau_vente (bordereau_id, vente_id, part_organisme, ajoute_par)
                SELECT $1, $2, po.part_organisme_retenue, $3 FROM v_vente_part_organisme po WHERE po.vente_id = $2
                """, cx, tx))
            {
                ins.Parameters.AddWithValue(bordereauId);
                ins.Parameters.AddWithValue(venteId);
                ins.Parameters.AddWithValue(utilisateurId);
                try
                {
                    n += await ins.ExecuteNonQueryAsync(ct);
                }
                catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
                {
                    return 0;   // vente déjà sur un autre bordereau (ux_bordereau_vente_active) : rien n'est gardé
                }
            }
        }
        if (n != ventes.Count) return n;
        await tx.CommitAsync(ct);
        return n;
    }

    public async Task<bool> RetirerVenteAsync(long bordereauId, long venteId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var del = new NpgsqlCommand("""
            DELETE FROM bordereau_vente bv USING bordereau b
            WHERE bv.bordereau_id = $1 AND bv.vente_id = $2 AND b.id = bv.bordereau_id AND b.statut = 'preparation' AND bv.rejete_le IS NULL
            """, cx, tx))
        {
            del.Parameters.AddWithValue(bordereauId);
            del.Parameters.AddWithValue(venteId);
            if (await del.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await using (var maj = new NpgsqlCommand("UPDATE vente SET bordereau_id = NULL WHERE id = $2 AND bordereau_id = $1", cx, tx))
        {
            maj.Parameters.AddWithValue(bordereauId);
            maj.Parameters.AddWithValue(venteId);
            await maj.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> DeposerAsync(long id, DateOnly date, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE bordereau SET statut = 'depose', date_envoi = $2, depose_le = now(), depose_par = $3
            WHERE id = $1 AND statut = 'preparation' AND EXISTS (SELECT 1 FROM bordereau_vente bv WHERE bv.bordereau_id = $1)
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(date);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RejeterAsync(long bordereauId, long venteId, string motifCode, string? texte, decimal montant, long utilisateurId,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE bordereau_vente bv SET rejete_le = now(), rejete_par = $3, rejet_motif_code = $4, rejet_texte = $5, montant_rejete = $6
            FROM bordereau b
            WHERE bv.bordereau_id = $1 AND bv.vente_id = $2 AND b.id = bv.bordereau_id AND bv.rejete_le IS NULL
              AND b.statut IN ('depose', 'paye_partiel', 'paye')
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(bordereauId);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(motifCode);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(montant);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        await RecalculerStatutAsync(cx, tx, bordereauId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> TraiterRejetAsync(long bordereauId, long venteId, string suite, long utilisateurId, string reference, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        long? patientId;
        decimal montant;
        await using (var lire = new NpgsqlCommand("""
            SELECT v.patient_id, bv.montant_rejete FROM bordereau_vente bv JOIN vente v ON v.id = bv.vente_id
            WHERE bv.bordereau_id = $1 AND bv.vente_id = $2 AND bv.rejete_le IS NOT NULL AND bv.suite IS NULL FOR UPDATE OF bv
            """, cx, tx))
        {
            lire.Parameters.AddWithValue(bordereauId);
            lire.Parameters.AddWithValue(venteId);
            await using var r = await lire.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return false;
            (patientId, montant) = (r.IsDBNull(0) ? null : r.GetInt64(0), r.GetDecimal(1));
        }
        var avecCredit = suite == RegleBordereau.Patient;
        if (avecCredit)
        {
            if (patientId is null) return false;
            // Dette du patient augmentée : le déclencheur écrit le mouvement de crédit (rattaché à la vente, compté par v_credit_accorde)
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, venteId, utilisateurId, null, reference, ct);
            await using var maj = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit + $2 WHERE id = $1", cx, tx);
            maj.Parameters.AddWithValue(patientId.Value);
            maj.Parameters.AddWithValue(montant);
            await maj.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand($"""
            UPDATE bordereau_vente SET suite = $3, suite_le = now(), suite_par = $4,
                   credit_mouvement_id = {(avecCredit ? "currval(pg_get_serial_sequence('credit_mouvement', 'id'))" : "NULL")}
            WHERE bordereau_id = $1 AND vente_id = $2 AND rejete_le IS NOT NULL AND suite IS NULL
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(bordereauId);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.AddWithValue(suite);
            cmd.Parameters.AddWithValue(utilisateurId);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        if (suite == RegleBordereau.Redeposer)
        {
            // La vente redevient « à déposer » (elle reste non annulable : elle a figuré sur un bordereau déposé)
            await using var libre = new NpgsqlCommand("UPDATE vente SET bordereau_id = NULL WHERE id = $2 AND bordereau_id = $1", cx, tx);
            libre.Parameters.AddWithValue(bordereauId);
            libre.Parameters.AddWithValue(venteId);
            await libre.ExecuteNonQueryAsync(ct);
        }
        await RecalculerStatutAsync(cx, tx, bordereauId, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>Statut d'un bordereau déposé après un versement ou un rejet (RegleBordereau.StatutApres), dans la transaction en cours.</summary>
    internal static async Task RecalculerStatutAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long bordereauId, CancellationToken ct)
    {
        string statut;
        decimal attendu, verse, rejete;
        int aTraiter;
        await using (var lire = new NpgsqlCommand("SELECT statut, attendu, verse, rejete, rejets_a_traiter FROM v_bordereau WHERE id = $1", cx, tx))
        {
            lire.Parameters.AddWithValue(bordereauId);
            await using var r = await lire.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;
            (statut, attendu, verse, rejete, aTraiter) = (r.GetString(0), r.GetDecimal(1), r.GetDecimal(2), r.GetDecimal(3), r.GetInt32(4));
        }
        var nouveau = RegleBordereau.StatutApres(statut, attendu, verse, rejete, aTraiter);
        if (nouveau == statut) return;
        await using var maj = new NpgsqlCommand("UPDATE bordereau SET statut = $2 WHERE id = $1", cx, tx);
        maj.Parameters.AddWithValue(bordereauId);
        maj.Parameters.AddWithValue(nouveau);
        await maj.ExecuteNonQueryAsync(ct);
    }

    private static Bordereau Lire(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return new Bordereau(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), S(5), r.GetString(6),
            DateOnly.FromDateTime(r.GetDateTime(7)), DateOnly.FromDateTime(r.GetDateTime(8)), r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)),
            r.GetString(10), r.IsDBNull(11) ? null : r.GetDecimal(11), r.GetInt32(12), r.GetDecimal(13), r.GetDecimal(14), r.GetInt32(15),
            r.GetDecimal(16), r.GetDecimal(17), r.GetDateTime(18), S(19), !r.IsDBNull(20) && r.GetBoolean(20));
    }
}
