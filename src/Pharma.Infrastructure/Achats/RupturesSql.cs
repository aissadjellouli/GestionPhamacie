using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Achats;

/// <summary>Morceaux SQL partagés : stock vendable (lots ni périmés ni bloqués) et disponibilité déclarée par un grossiste actif.</summary>
internal static class SqlDisponibilite
{
    public const string Stock = """
        COALESCE((SELECT sum(l.quantite_disponible) FROM lot l
                  WHERE l.produit_id = {0} AND l.date_peremption > current_date AND NOT l.bloque), 0)::int
        """;
    public const string Grossiste = """
        EXISTS (SELECT 1 FROM catalogue_grossiste c JOIN fournisseur f ON f.id = c.fournisseur_id
                WHERE c.produit_id = {0} AND c.disponible AND f.actif)
        """;

    public static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    public static NpgsqlParameter Booleen(bool? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean };
}

/// <summary>Ruptures chez les grossistes (116).</summary>
public sealed class RupturesGrossistesSql(NpgsqlDataSource db) : IRupturesGrossistes
{
    public async Task<Page<RuptureProduit>> PartoutAsync(string? recherche, DemandePage page, CancellationToken ct = default)
    {
        // Rupture partout : au moins un grossiste actif le déclare indisponible et aucun ne le déclare disponible
        await using var cmd = db.CreateCommand($"""
            WITH r AS (
                SELECT c.produit_id, count(*) FILTER (WHERE c.disponible = false) AS grossistes
                FROM catalogue_grossiste c JOIN fournisseur f ON f.id = c.fournisseur_id AND f.actif
                GROUP BY c.produit_id
                HAVING count(*) FILTER (WHERE c.disponible = false) > 0 AND count(*) FILTER (WHERE c.disponible) = 0
            )
            SELECT p.id, p.nom_commercial, r.grossistes::int,
                   COALESCE((SELECT max(rg.debut) FROM rupture_grossiste rg WHERE rg.produit_id = p.id AND rg.fin IS NULL),
                            (SELECT max(c.maj_le) FROM catalogue_grossiste c WHERE c.produit_id = p.id)),
                   {string.Format(SqlDisponibilite.Stock, "p.id")},
                   (SELECT count(*) FROM demande_patient d WHERE d.produit_id = p.id AND d.statut IN ('en_attente', 'prevenu'))::int,
                   count(*) OVER ()
            FROM r JOIN produit p ON p.id = r.produit_id
            WHERE $1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%'
            ORDER BY 4 DESC, p.nom_commercial
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.Add(SqlDisponibilite.Texte(recherche));
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<RuptureProduit>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(new RuptureProduit(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.GetFieldValue<DateTime>(3), r.GetInt32(4), r.GetInt32(5)));
            total = (int)r.GetInt64(6);
        }
        return new Page<RuptureProduit>(lignes, total, page.Page, page.Taille);
    }

    public async Task<Page<PeriodeRupture>> HistoriqueAsync(string? recherche, bool? enCours, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT rg.id, rg.produit_id, p.nom_commercial, rg.fournisseur_id, f.raison_sociale, rg.debut, rg.fin, count(*) OVER ()
            FROM rupture_grossiste rg
            JOIN produit p ON p.id = rg.produit_id
            JOIN fournisseur f ON f.id = rg.fournisseur_id
            WHERE ($1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%' OR f.raison_sociale ILIKE '%' || $1 || '%')
              AND ($2::boolean IS NULL OR (rg.fin IS NULL) = $2)
            ORDER BY COALESCE(rg.fin, rg.debut) DESC, rg.id DESC
            LIMIT $3 OFFSET $4
            """);
        cmd.Parameters.Add(SqlDisponibilite.Texte(recherche));
        cmd.Parameters.Add(SqlDisponibilite.Booleen(enCours));
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<PeriodeRupture>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(new PeriodeRupture(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetFieldValue<DateTime>(5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateTime>(6)));
            total = (int)r.GetInt64(7);
        }
        return new Page<PeriodeRupture>(lignes, total, page.Page, page.Taille);
    }
}

/// <summary>Patients en attente d'un produit et consentement SMS (116) ; produits dus des ventes en attente (094).</summary>
public sealed class DemandesPatientsSql(NpgsqlDataSource db) : IDemandesPatients
{
    private static readonly string Selection = $"""
        SELECT d.id, d.patient_id, btrim(pa.nom || ' ' || COALESCE(pa.prenom, '')), pa.telephone, pa.consentement_sms, pa.consentement_sms_le,
               d.produit_id, p.nom_commercial, d.quantite, d.cree_le, d.statut, d.prevenu_le, d.prevenu_canal, d.sms_erreur,
               {string.Format(SqlDisponibilite.Stock, "d.produit_id")} AS stock_dispo,
               {string.Format(SqlDisponibilite.Grossiste, "d.produit_id")} AS dispo_grossiste
        FROM demande_patient d
        JOIN patient pa ON pa.id = d.patient_id
        JOIN produit p ON p.id = d.produit_id
        """;

    private static DemandePatient Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetBoolean(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTime>(5), r.GetInt64(6), r.GetString(7), r.GetInt32(8), r.GetFieldValue<DateTime>(9),
        r.GetString(10), r.IsDBNull(11) ? null : r.GetFieldValue<DateTime>(11), r.IsDBNull(12) ? null : r.GetString(12),
        r.IsDBNull(13) ? null : r.GetString(13), r.GetInt32(14), r.GetBoolean(15));

    private async Task<bool> ExisteAsync(string sql, long id, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(sql);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public Task<bool> PatientExisteAsync(long patientId, CancellationToken ct = default) => ExisteAsync("SELECT 1 FROM patient WHERE id = $1", patientId, ct);
    public Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default) => ExisteAsync("SELECT 1 FROM produit WHERE id = $1", produitId, ct);

    public async Task<long?> DemandeOuverteAsync(long patientId, long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id FROM demande_patient WHERE patient_id = $1 AND produit_id = $2 AND statut IN ('en_attente', 'prevenu') ORDER BY id LIMIT 1");
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }

    public async Task<long> CreerAsync(long patientId, long produitId, int quantite, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "INSERT INTO demande_patient (patient_id, produit_id, quantite, cree_par) VALUES ($1, $2, $3, $4) RETURNING id");
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(quantite);
        cmd.Parameters.AddWithValue(utilisateurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<DemandePatient?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Selection + " WHERE d.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<Page<DemandePatient>> PageAsync(string? recherche, string? statut, DemandePage page, CancellationToken ct = default)
    {
        // statut « ouvertes » : en attente ou prévenu (défaut de l'écran)
        await using var cmd = db.CreateCommand($"""
            SELECT x.*, count(*) OVER () FROM ({Selection}
                WHERE ($1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%' OR pa.nom ILIKE '%' || $1 || '%' OR pa.prenom ILIKE '%' || $1 || '%')
                  AND ($2::text IS NULL OR ($2 = 'ouvertes' AND d.statut IN ('en_attente', 'prevenu')) OR d.statut = $2)) x
            ORDER BY x.cree_le DESC, x.id DESC
            LIMIT $3 OFFSET $4
            """);
        cmd.Parameters.Add(SqlDisponibilite.Texte(recherche));
        cmd.Parameters.Add(SqlDisponibilite.Texte(statut));
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<DemandePatient>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(Lire(r));
            total = (int)r.GetInt64(16);
        }
        return new Page<DemandePatient>(lignes, total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<DemandePatient>> DisponiblesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT * FROM ({Selection} WHERE d.statut = 'en_attente') x
            WHERE x.stock_dispo > 0 OR x.dispo_grossiste
            ORDER BY x.cree_le LIMIT 200
            """);
        var liste = new List<DemandePatient>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<IReadOnlyList<ProduitDuEnAttente>> ProduitsDusDisponiblesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT * FROM (
                SELECT a.id, a.libelle, p.id AS produit_id, p.nom_commercial, GREATEST(COALESCE((j->>'Quantite')::int, 1), 1) AS quantite, a.cree_le,
                       {string.Format(SqlDisponibilite.Stock, "p.id")} AS stock_dispo, {string.Format(SqlDisponibilite.Grossiste, "p.id")} AS dispo_grossiste
                FROM panier_attente a
                CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(a.produits_dus) = 'array' THEN a.produits_dus ELSE '[]'::jsonb END) j
                JOIN produit p ON p.id = (j->>'ProduitId')::bigint
                WHERE a.produits_dus IS NOT NULL AND (j->>'ProduitId') ~ '^[0-9]+$'
            ) x
            WHERE x.stock_dispo > 0 OR x.dispo_grossiste
            ORDER BY x.cree_le LIMIT 200
            """);
        var liste = new List<ProduitDuEnAttente>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitDuEnAttente(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetInt32(4), r.GetFieldValue<DateTime>(5),
                r.GetInt32(6), r.GetBoolean(7)));
        return liste;
    }

    public async Task MarquerPrevenuAsync(long id, string canal, long utilisateurId, DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE demande_patient SET statut = 'prevenu', prevenu_le = $2, prevenu_par = $3, prevenu_canal = $4, sms_erreur = NULL
            WHERE id = $1 AND statut IN ('en_attente', 'prevenu')
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(canal);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterEchecSmsAsync(long id, string erreur, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE demande_patient SET sms_erreur = left($2, 500) WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(erreur);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> CloturerAsync(long id, string statut, long utilisateurId, DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE demande_patient SET statut = $2, cloture_le = $3, cloture_par = $4
            WHERE id = $1 AND statut IN ('en_attente', 'prevenu')
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(statut);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<(bool Accord, DateTime? Le)?> ConsentementAsync(long patientId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT consentement_sms, consentement_sms_le FROM patient WHERE id = $1");
        cmd.Parameters.AddWithValue(patientId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTime>(1));
    }

    public async Task EnregistrerConsentementAsync(long patientId, bool accord, DateTime quandUtc, CancellationToken ct = default)
    {
        // Date de l'accord ou du retrait
        await using var cmd = db.CreateCommand("""
            UPDATE patient SET consentement_sms = $2, consentement_sms_le = $3 WHERE id = $1 AND consentement_sms IS DISTINCT FROM $2
            """);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(accord);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc));
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Formulaire d'accord SMS (script 123) : « formulaire signé reçu » sur la fiche patient, texte réglé par le titulaire.</summary>
public sealed class FormulaireAccordSmsSql(NpgsqlDataSource db) : IFormulaireAccordSmsDepot
{
    public async Task<(string Nom, string? Telephone)?> PatientAsync(long patientId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT btrim(nom || ' ' || COALESCE(prenom, '')), telephone FROM patient WHERE id = $1");
        cmd.Parameters.AddWithValue(patientId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task<(bool Recu, DateTime? Le)?> RecuAsync(long patientId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT formulaire_sms_recu, formulaire_sms_recu_le FROM patient WHERE id = $1");
        cmd.Parameters.AddWithValue(patientId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return (r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetFieldValue<DateTime>(1));
    }

    public async Task EnregistrerRecuAsync(long patientId, bool recu, DateTime quandUtc, CancellationToken ct = default)
    {
        // Date de réception (ou du retrait de la case), seulement si la valeur change
        await using var cmd = db.CreateCommand("""
            UPDATE patient SET formulaire_sms_recu = $2, formulaire_sms_recu_le = $3 WHERE id = $1 AND formulaire_sms_recu IS DISTINCT FROM $2
            """);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(recu);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<string?> TexteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT texte FROM parametre_formulaire_sms WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task EnregistrerTexteAsync(string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_formulaire_sms (id, texte, modifie_le, modifie_par) VALUES (1, $1, now(), $2)
            ON CONFLICT (id) DO UPDATE SET texte = EXCLUDED.texte, modifie_le = EXCLUDED.modifie_le, modifie_par = EXCLUDED.modifie_par
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Paramètre de substitution, liste blanche et « fabriqué en Algérie » (116).</summary>
public sealed class SubstitutionSql(NpgsqlDataSource db) : ISubstitution
{
    public async Task<ParametreSubstitution> ParametreAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.equivalent_exact_seulement, s.modifie_le, u.nom_complet
            FROM parametre_substitution s LEFT JOIN utilisateur u ON u.id = s.modifie_par WHERE s.id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        // Ligne absente : réglage par défaut (équivalent exact seulement)
        if (!await r.ReadAsync(ct)) return new ParametreSubstitution();
        return new ParametreSubstitution(r.GetBoolean(0), r.GetFieldValue<DateTime>(1), r.IsDBNull(2) ? null : r.GetString(2));
    }

    public async Task EnregistrerParametreAsync(bool equivalentExactSeulement, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_substitution (id, equivalent_exact_seulement, modifie_le, modifie_par) VALUES (1, $1, now(), $2)
            ON CONFLICT (id) DO UPDATE SET equivalent_exact_seulement = EXCLUDED.equivalent_exact_seulement, modifie_le = now(),
                modifie_par = EXCLUDED.modifie_par
            """);
        cmd.Parameters.AddWithValue(equivalentExactSeulement);
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<Page<LienListeBlanche>> ListeBlancheAsync(string? recherche, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT w.produit_id, p.nom_commercial, w.remplacant_id, r.nom_commercial, w.cree_le, u.nom_complet, count(*) OVER ()
            FROM substitution_liste_blanche w
            JOIN produit p ON p.id = w.produit_id
            JOIN produit r ON r.id = w.remplacant_id
            LEFT JOIN utilisateur u ON u.id = w.cree_par
            WHERE $1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%' OR r.nom_commercial ILIKE '%' || $1 || '%'
            ORDER BY p.nom_commercial, r.nom_commercial
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.Add(SqlDisponibilite.Texte(recherche));
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<LienListeBlanche>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(new LienListeBlanche(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetFieldValue<DateTime>(4),
                r.IsDBNull(5) ? null : r.GetString(5)));
            total = (int)r.GetInt64(6);
        }
        return new Page<LienListeBlanche>(lignes, total, page.Page, page.Taille);
    }

    public async Task<bool> AjouterListeBlancheAsync(long produitId, long remplacantId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO substitution_liste_blanche (produit_id, remplacant_id, cree_par) VALUES ($1, $2, $3) ON CONFLICT DO NOTHING
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(remplacantId);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RetirerListeBlancheAsync(long produitId, long remplacantId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("DELETE FROM substitution_liste_blanche WHERE produit_id = $1 AND remplacant_id = $2");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(remplacantId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT 1 FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<Page<ProduitFabrication>> FabricationAsync(string? recherche, bool? locale, DemandePage page, CancellationToken ct = default)
    {
        // Filtre sur la valeur retenue (saisie, sinon nomenclature) ; produits actifs
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial, lab.nom, p.fabrication_locale,
                   CASE WHEN fl.source = 'nomenclature' THEN fl.fabrication_locale
                        ELSE (SELECT upper(btrim(np.pays)) IN ('ALGERIE', 'ALGÉRIE') FROM nomenclature_produit np
                              WHERE p.num_enregistrement IS NOT NULL AND np.num_enregistrement = p.num_enregistrement
                                AND btrim(COALESCE(np.pays, '')) <> '' ORDER BY np.version_id DESC LIMIT 1) END,
                   count(*) OVER ()
            FROM produit p
            JOIN v_produit_fabrication_locale fl ON fl.produit_id = p.id
            LEFT JOIN laboratoire lab ON lab.id = p.laboratoire_id
            WHERE p.actif
              AND ($1::text IS NULL OR p.nom_commercial ILIKE '%' || $1 || '%')
              AND ($2::boolean IS NULL OR fl.fabrication_locale IS NOT DISTINCT FROM $2)
            ORDER BY p.nom_commercial, p.id
            LIMIT $3 OFFSET $4
            """);
        cmd.Parameters.Add(SqlDisponibilite.Texte(recherche));
        cmd.Parameters.Add(SqlDisponibilite.Booleen(locale));
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<ProduitFabrication>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(new ProduitFabrication(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetBoolean(4)));
            total = (int)r.GetInt64(5);
        }
        return new Page<ProduitFabrication>(lignes, total, page.Page, page.Taille);
    }

    public async Task<bool> ModifierFabricationAsync(long produitId, bool? fabricationLocale, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET fabrication_locale = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.Add(SqlDisponibilite.Booleen(fabricationLocale));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
