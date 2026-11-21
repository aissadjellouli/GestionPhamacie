using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Chifa;

/// <summary>
/// Informations Chifa et exclusion d'une vente (database/migration/100_volet_chifa_centres_payeurs_exclusion.sql) : date des soins, centre payeur,
/// saisi dans Chifa par, majoration, exclusion du traitement (colonnes chifa_exclue_*, sans nouveau statut de vente).
/// </summary>
public sealed class VentesChifaSql(NpgsqlDataSource db) : IVentesChifa
{
    private const string Colonnes = """
        v.id, v.numero, v.date_vente, v.canal_organisme, v.statut, v.bordereau_id IS NOT NULL, v.chifa_rattache_le IS NOT NULL,
        v.patient_id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), p.num_assure, v.organisme_id, o.code, u.nom_complet, v.utilisateur_id,
        v.montant_total, v.part_organisme, v.chifa_facture,
        v.chifa_date_soins, v.centre_payeur_id, v.chifa_saisi_par, v.chifa_majoration_code, v.chifa_majoration_montant,
        cp.code || ' · ' || cp.nom, sp.nom_complet,
        COALESCE((SELECT mr.libelle FROM motif_reglable mr WHERE mr.liste = 'majoration_chifa' AND mr.code = v.chifa_majoration_code), v.chifa_majoration_code),
        v.chifa_exclue_le, ep.nom_complet, v.chifa_exclusion_motif_code,
        CASE WHEN v.chifa_exclusion_motif_code IS NULL THEN NULL
             WHEN v.chifa_exclusion_motif_code = 'autre' AND v.chifa_exclusion_texte IS NOT NULL THEN 'Autre : ' || v.chifa_exclusion_texte
             ELSE COALESCE((SELECT mr.libelle FROM motif_reglable mr WHERE mr.liste = 'chifa_exclusion' AND mr.code = v.chifa_exclusion_motif_code),
                           v.chifa_exclusion_motif_code) END,
        p.centre_payeur_id
        FROM vente v
        JOIN utilisateur u ON u.id = v.utilisateur_id
        LEFT JOIN patient p ON p.id = v.patient_id
        LEFT JOIN organisme o ON o.id = v.organisme_id
        LEFT JOIN centre_payeur cp ON cp.id = v.centre_payeur_id
        LEFT JOIN utilisateur sp ON sp.id = v.chifa_saisi_par
        LEFT JOIN utilisateur ep ON ep.id = v.chifa_exclue_par
        """;

    // Recherche : n° de vente, patient, n° d'assuré (chiffres seuls), n° de facture Chifa
    private const string Filtre = """
        v.canal_organisme = 'chifa' AND v.statut <> 'en_cours'
        AND ($1::text IS NULL OR v.numero ILIKE '%' || $1 || '%' OR trim(p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
             OR COALESCE(v.chifa_facture, '') ILIKE '%' || $1 || '%'
             OR (regexp_replace($1, '\D', '', 'g') <> '' AND regexp_replace(COALESCE(p.num_assure, ''), '\D', '', 'g') LIKE '%' || regexp_replace($1, '\D', '', 'g') || '%'))
        AND ($2::bigint IS NULL OR v.centre_payeur_id = $2)
        AND ($3::text = 'tous' OR ($3 = 'oui') = (v.chifa_exclue_le IS NOT NULL))
        """;

    public async Task<VenteChifa?> LireAsync(long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE v.id = $1");
        cmd.Parameters.AddWithValue(venteId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<Page<VenteChifa>> ListerAsync(FiltreVentesChifa filtre, DemandePage page, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.CentrePayeurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            c.Parameters.Add(new NpgsqlParameter { Value = filtre.Exclues, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM vente v LEFT JOIN patient p ON p.id = v.patient_id WHERE {Filtre}", cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {Filtre} ORDER BY COALESCE(v.chifa_exclue_le, v.date_vente) DESC, v.id DESC LIMIT $4 OFFSET $5", cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<VenteChifa>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return new Page<VenteChifa>(liste, total, page.Page, page.Taille);
    }

    public async Task<bool> EnregistrerInfosAsync(long venteId, InfosChifa infos, bool retenirCentre, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE vente SET chifa_date_soins = $2, centre_payeur_id = $3, chifa_saisi_par = $4, chifa_majoration_code = $5, chifa_majoration_montant = $6
            WHERE id = $1 AND canal_organisme = 'chifa' AND statut = 'validee' AND bordereau_id IS NULL
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)infos.DateSoins ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)infos.CentrePayeurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)infos.SaisiPar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)infos.MajorationCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(infos.MajorationMontant);
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        if (retenirCentre)
        {
            await using var maj = new NpgsqlCommand(
                "UPDATE patient p SET centre_payeur_id = $2 FROM vente v WHERE v.id = $1 AND p.id = v.patient_id", cx, tx);
            maj.Parameters.AddWithValue(venteId);
            maj.Parameters.AddWithValue(infos.CentrePayeurId!.Value);
            await maj.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    public async Task<bool> ExclureAsync(long venteId, string motifCode, string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE vente SET chifa_exclue_le = now(), chifa_exclue_par = $2, chifa_exclusion_motif_code = $3, chifa_exclusion_texte = $4
            WHERE id = $1 AND canal_organisme = 'chifa' AND statut = 'validee' AND bordereau_id IS NULL AND chifa_exclue_le IS NULL
            """);
        cmd.Parameters.AddWithValue(venteId);
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(motifCode);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RetablirAsync(long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE vente SET chifa_exclue_le = NULL, chifa_exclue_par = NULL, chifa_exclusion_motif_code = NULL, chifa_exclusion_texte = NULL
            WHERE id = $1 AND chifa_exclue_le IS NOT NULL
            """);
        cmd.Parameters.AddWithValue(venteId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    // Retours avec part organisme des ventes Chifa (script 155) : écriture du retour qui porte les lignes, produits rendus, part retirée
    private const string ColonnesRetours = """
        rc.id, rc.cree_le, u.nom_complet, v.id, v.numero, v.date_vente, NULLIF(trim(p.nom || ' ' || COALESCE(p.prenom, '')), ''), p.num_assure,
        v.chifa_facture,
        (SELECT string_agg(rl.quantite || ' × ' || COALESCE(pr.nom_commercial, l.libelle), ' ; ' ORDER BY rl.id)
         FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id LEFT JOIN produit pr ON pr.id = l.produit_id
         WHERE rl.retour_id = rc.id),
        (SELECT sum(rl.montant_organisme) FROM retour_client_ligne rl WHERE rl.retour_id = rc.id),
        EXISTS (SELECT 1 FROM bordereau_vente bv JOIN bordereau b ON b.id = bv.bordereau_id WHERE bv.vente_id = v.id AND b.statut <> 'preparation'),
        rc.chifa_corrige_le, cu.nom_complet
        FROM retour_client rc
        JOIN vente v ON v.id = rc.vente_id
        JOIN utilisateur u ON u.id = rc.utilisateur_id
        LEFT JOIN patient p ON p.id = v.patient_id
        LEFT JOIN utilisateur cu ON cu.id = rc.chifa_corrige_par
        """;

    private const string FiltreRetours = """
        v.canal_organisme = 'chifa'
        AND EXISTS (SELECT 1 FROM retour_client_ligne rl WHERE rl.retour_id = rc.id AND rl.montant_organisme > 0)
        AND ($1::text IS NULL OR v.numero ILIKE '%' || $1 || '%' OR trim(p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
             OR COALESCE(v.chifa_facture, '') ILIKE '%' || $1 || '%'
             OR (regexp_replace($1, '\D', '', 'g') <> '' AND regexp_replace(COALESCE(p.num_assure, ''), '\D', '', 'g') LIKE '%' || regexp_replace($1, '\D', '', 'g') || '%'))
        AND ($2::text = 'tous' OR ($2 = 'corriges') = (rc.chifa_corrige_le IS NOT NULL))
        """;

    public async Task<Page<RetourACorrigerChifa>> RetoursACorrigerAsync(FiltreRetoursACorriger filtre, DemandePage page, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            c.Parameters.Add(new NpgsqlParameter { Value = filtre.Etat, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"""
            SELECT count(*) FROM retour_client rc JOIN vente v ON v.id = rc.vente_id LEFT JOIN patient p ON p.id = v.patient_id WHERE {FiltreRetours}
            """, cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {ColonnesRetours} WHERE {FiltreRetours} ORDER BY rc.cree_le DESC, rc.id DESC LIMIT $3 OFFSET $4", cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<RetourACorrigerChifa>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(LireRetour(r));
        return new Page<RetourACorrigerChifa>(liste, total, page.Page, page.Taille);
    }

    public async Task<RetourACorrigerChifa?> MarquerRetourCorrigeAsync(long retourId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using (var maj = new NpgsqlCommand($"""
            UPDATE retour_client rc SET chifa_corrige_le = now(), chifa_corrige_par = $3
            FROM vente v LEFT JOIN patient p ON p.id = v.patient_id
            WHERE rc.id = $4 AND v.id = rc.vente_id AND rc.chifa_corrige_le IS NULL AND {FiltreRetours}
            """, cx, tx))
        {
            maj.Parameters.Add(new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            maj.Parameters.Add(new NpgsqlParameter { Value = FiltreRetoursACorriger.ACorriger, NpgsqlDbType = NpgsqlDbType.Text });
            maj.Parameters.AddWithValue(utilisateurId);
            maj.Parameters.AddWithValue(retourId);
            if (await maj.ExecuteNonQueryAsync(ct) != 1) return null;
        }
        RetourACorrigerChifa lu;
        await using (var cmd = new NpgsqlCommand($"SELECT {ColonnesRetours} WHERE rc.id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(retourId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            lu = LireRetour(r);
        }
        await tx.CommitAsync(ct);
        return lu;
    }

    private static RetourACorrigerChifa LireRetour(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetDateTime(1), r.GetString(2), r.GetInt64(3), r.GetString(4), r.GetDateTime(5),
            r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? "" : r.GetString(9), r.IsDBNull(10) ? 0 : r.GetDecimal(10), r.GetBoolean(11),
            r.IsDBNull(12) ? null : r.GetDateTime(12), r.IsDBNull(13) ? null : r.GetString(13));

    public async Task<string?> EmployeActifAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_complet FROM utilisateur WHERE id = $1 AND actif");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<IReadOnlyList<(long Id, string Nom)>> EmployesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, nom_complet FROM utilisateur WHERE actif ORDER BY nom_complet, id");
        var liste = new List<(long, string)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add((r.GetInt64(0), r.GetString(1)));
        return liste;
    }

    private static VenteChifa Lire(NpgsqlDataReader r)
    {
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        long? L(int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
        return new VenteChifa(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3), r.GetString(4), r.GetBoolean(5), r.GetBoolean(6),
            L(7), S(8), S(9), L(10), S(11), r.GetString(12), r.GetInt64(13), r.GetDecimal(14), r.GetDecimal(15), S(16),
            new InfosChifa(r.IsDBNull(17) ? null : DateOnly.FromDateTime(r.GetDateTime(17)), L(18), L(19), S(20), r.GetDecimal(21)),
            S(22), S(23), S(24), r.IsDBNull(25) ? null : r.GetDateTime(25), S(26), S(27), S(28), L(29));
    }
}

/// <summary>Centres payeurs (script 100) : jamais supprimés ; code unique par organisme (uq_centre_payeur_code).</summary>
public sealed class CentresPayeursSql(NpgsqlDataSource db) : ICentresPayeurs
{
    private const string Colonnes = "c.id, c.organisme_id, o.code, c.code, c.nom, c.actif FROM centre_payeur c JOIN organisme o ON o.id = c.organisme_id";

    public async Task<Page<CentrePayeur>> ListerAsync(long? organismeId, bool? actif, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        const string Filtre = """
            ($1::bigint IS NULL OR c.organisme_id = $1) AND ($2::boolean IS NULL OR c.actif = $2)
            AND ($3::text IS NULL OR c.code ILIKE '%' || $3 || '%' OR c.nom ILIKE '%' || $3 || '%')
            """;
        await using var cx = await db.OpenConnectionAsync(ct);
        void Parametres(NpgsqlCommand c)
        {
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)organismeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)actif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        }
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM centre_payeur c WHERE {Filtre}", cx))
        {
            Parametres(compte);
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {Filtre} ORDER BY o.code, c.code LIMIT $4 OFFSET $5", cx);
        Parametres(cmd);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<CentrePayeur>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return new Page<CentrePayeur>(liste, total, page.Page, page.Taille);
    }

    public async Task<CentrePayeur?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} WHERE c.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<string?> CodeOrganismeAsync(long organismeId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT code FROM organisme WHERE id = $1");
        cmd.Parameters.AddWithValue(organismeId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<long> EnregistrerAsync(long? id, long organismeId, string code, string nom, bool actif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(id is null
            ? "INSERT INTO centre_payeur (organisme_id, code, nom, actif) VALUES ($1, $2, $3, $4) RETURNING id"
            : "UPDATE centre_payeur SET organisme_id = $1, code = $2, nom = $3, actif = $4 WHERE id = $5 RETURNING id");
        cmd.Parameters.AddWithValue(organismeId);
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.AddWithValue(actif);
        if (id is { } i) cmd.Parameters.AddWithValue(i);
        try
        {
            return await cmd.ExecuteScalarAsync(ct) is long nouvel ? nouvel : throw new RegleMetierException("Centre payeur introuvable.", "centre_payeur");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le code de centre « {code} » existe déjà pour cet organisme.", "code");
        }
    }

    private static CentrePayeur Lire(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetBoolean(5));
}
