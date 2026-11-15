using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Circuit « la caisse d'abord » (2026-11-15, database/migration/095_rapprochement_volet.sql) : ventes Chifa encaissées en attente de leur
/// volet, rattachement du volet scanné, volets rattachés avec écart. Une vente en attente : canal chifa, sans n° de facture Chifa, validée,
/// organisme CNAS ou CASNOS (index ix_vente_chifa_attente).
/// </summary>
public sealed class RapprochementsVoletSql(NpgsqlDataSource db) : IRapprochementsVolet
{
    private const string EnAttente = """
        v.canal_organisme = 'chifa' AND v.chifa_facture IS NULL AND v.statut = 'validee'
        -- Vente exclue du traitement (script 100) : ni en attente, ni candidate au rattachement
        AND v.chifa_exclue_le IS NULL
        AND EXISTS (SELECT 1 FROM organisme oc WHERE oc.id = v.organisme_id AND upper(oc.code) IN ('CNAS', 'CASNOS'))
        """;

    private const string Colonnes = """
        v.id, v.numero, v.date_vente, v.patient_id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), p.num_assure, o.code,
        CASE WHEN v.reste_saisi THEN NULL ELSE v.taux_tiers_payant END, v.montant_total, v.part_organisme, u.nom_complet,
        v.part_patient, COALESCE(p.solde_credit, 0),
        CASE WHEN v.chifa_attente_motif_code IS NULL THEN NULL
             WHEN v.chifa_attente_motif_code = 'autre' AND v.chifa_attente_motif_texte IS NOT NULL THEN 'Autre : ' || v.chifa_attente_motif_texte
             ELSE COALESCE((SELECT mr.libelle FROM motif_reglable mr WHERE mr.liste = 'volet_absent' AND mr.code = v.chifa_attente_motif_code),
                           v.chifa_attente_motif_code) END,
        v.date_vente < now() - make_interval(days => COALESCE((SELECT pc.delai_volet_chifa_jours FROM parametre_caisse pc WHERE pc.id = 1), 7)),
        -- Majoration Chifa (script 100) : ajoutée des deux côtés de la comparaison de part organisme (2026-11-15)
        v.chifa_majoration_montant
        FROM vente v
        JOIN utilisateur u ON u.id = v.utilisateur_id
        LEFT JOIN patient p ON p.id = v.patient_id
        LEFT JOIN organisme o ON o.id = v.organisme_id
        """;

    // Recherche : n° de vente, nom du patient, n° d'assuré (chiffres seuls)
    private const string Filtre = """
        ($1::text IS NULL OR v.numero ILIKE '%' || $1 || '%' OR trim(p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
         OR (regexp_replace($1, '\D', '', 'g') <> '' AND regexp_replace(COALESCE(p.num_assure, ''), '\D', '', 'g') LIKE '%' || regexp_replace($1, '\D', '', 'g') || '%'))
        """;

    public async Task<IReadOnlyList<VenteAttenteVolet>> CandidatesAsync(DateTime depuisUtc, int max, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {EnAttente} AND v.date_vente >= $1 ORDER BY v.date_vente DESC LIMIT $2", cx);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(depuisUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(max);
        var ventes = await LireAsync(cmd, ct);
        return await AvecLignesAsync(cx, ventes, ct);
    }

    public async Task<VenteAttenteVolet?> VenteAsync(long venteId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {EnAttente} AND v.id = $1", cx);
        cmd.Parameters.AddWithValue(venteId);
        var ventes = await LireAsync(cmd, ct);
        return ventes.Count == 0 ? null : (await AvecLignesAsync(cx, ventes, ct))[0];
    }

    public async Task<bool> RattacherAsync(RattachementVolet r, CancellationToken ct = default)
    {
        // Condition « encore en attente » dans la mise à jour : deux postes ne rattachent jamais deux volets à la même vente ;
        // un même volet n'est rattaché qu'à une vente (index unique ux_vente_chifa_facture). Régularisation de la part organisme
        // (script 097) dans la même transaction : dette du patient, puis la ligne de régularisation.
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await using var cmd = new NpgsqlCommand($"""
            UPDATE vente v SET chifa_facture = $2, chifa_date = $3, chifa_qr = $4, chifa_date_ordonnance = $5,
                               chifa_rattache_le = now(), chifa_rattache_par = $6,
                               chifa_ecart_motif = $7, chifa_ecart_detail = $8, chifa_ecart_part_organisme = $9
            WHERE v.id = $1 AND {EnAttente}
            """, cx, tx);
        cmd.Parameters.AddWithValue(r.VenteId);
        cmd.Parameters.AddWithValue(r.Qr.NumeroFacture);
        cmd.Parameters.AddWithValue(r.Qr.DateFacture);
        cmd.Parameters.AddWithValue(r.Qr.Brut);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)r.Qr.DateOrdonnance ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.AddWithValue(r.UtilisateurId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)r.Motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)r.DetailEcarts ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)r.EcartPartOrganisme ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        try
        {
            if (await cmd.ExecuteNonQueryAsync(ct) != 1) return false;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le volet Chifa n° {r.Qr.NumeroFacture} vient d’être rattaché à une autre vente.", "volet_chifa");
        }
        if (r.Regularisation is { } g) await RegulariserAsync(cx, tx, r, g, ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Régularisation de la part organisme (097) : à la charge du patient, dette augmentée (mouvement de crédit rattaché à la vente,
    /// compté par v_credit_accorde) ; avoir, dette diminuée si elle le couvre encore ; puis la ligne regularisation_part_organisme.
    /// </summary>
    private static async Task RegulariserAsync(NpgsqlConnection cx, NpgsqlTransaction tx, RattachementVolet r, RegularisationCalculee g, CancellationToken ct)
    {
        var delta = g.Sort == RegularisationPartOrganisme.ChargePatient ? g.MontantPatient : -g.DetteDiminuee;
        var avecMouvement = delta != 0 && r.PatientId is not null;
        if (avecMouvement)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, r.VenteId, r.UtilisateurId, null, r.Reference, ct);
            await using var maj = new NpgsqlCommand(
                "UPDATE patient SET solde_credit = solde_credit + $2 WHERE id = $1 AND solde_credit + $2 >= 0", cx, tx);
            maj.Parameters.AddWithValue(r.PatientId!.Value);
            maj.Parameters.AddWithValue(delta);
            if (await maj.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient vient de changer : rescannez le volet.", "regularisation");
        }
        await using var ins = new NpgsqlCommand($"""
            INSERT INTO regularisation_part_organisme (vente_id, organisme_id, part_organisme_vente, part_organisme_chifa, ecart, sort,
                montant_patient, montant_dette_diminuee, montant_a_rendre, montant_pharmacie, credit_mouvement_id, utilisateur_id)
            SELECT v.id, v.organisme_id, v.part_organisme, $2, $3, $4, $5, $6, $7, $8,
                   {(avecMouvement ? "currval(pg_get_serial_sequence('credit_mouvement', 'id'))" : "NULL")}, $9
            FROM vente v WHERE v.id = $1
            """, cx, tx);
        ins.Parameters.AddWithValue(r.VenteId);
        ins.Parameters.AddWithValue(g.PartOrganismeChifa);
        ins.Parameters.AddWithValue(g.Ecart);
        ins.Parameters.AddWithValue(g.Sort);
        ins.Parameters.AddWithValue(g.MontantPatient);
        ins.Parameters.AddWithValue(g.DetteDiminuee);
        ins.Parameters.AddWithValue(g.ARendre);
        ins.Parameters.AddWithValue(g.MontantPharmacie);
        ins.Parameters.AddWithValue(r.UtilisateurId);
        await ins.ExecuteNonQueryAsync(ct);
    }

    public async Task<(IReadOnlyList<VenteAttenteVolet> Lignes, int Total)> EnAttenteAsync(string? recherche, DemandePage page, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) FROM vente v LEFT JOIN patient p ON p.id = v.patient_id WHERE {EnAttente} AND {Filtre}", cx))
        {
            compte.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"SELECT {Colonnes} WHERE {EnAttente} AND {Filtre} ORDER BY v.date_vente, v.id LIMIT $2 OFFSET $3", cx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        return (await LireAsync(cmd, ct), total);
    }

    public async Task<(IReadOnlyList<VoletAvecEcart> Lignes, int Total)> AvecEcartAsync(string? recherche, DemandePage page, CancellationToken ct = default)
    {
        const string Base = """
            FROM vente v
            LEFT JOIN patient p ON p.id = v.patient_id
            LEFT JOIN utilisateur r ON r.id = v.chifa_rattache_par
            LEFT JOIN regularisation_part_organisme g ON g.vente_id = v.id
            WHERE (v.chifa_ecart_motif IS NOT NULL OR g.id IS NOT NULL) AND v.statut <> 'annulee'
            """;
        var filtre = Filtre + " OR v.chifa_facture ILIKE '%' || $1 || '%'";
        await using var cx = await db.OpenConnectionAsync(ct);
        int total;
        await using (var compte = new NpgsqlCommand($"SELECT count(*) {Base} AND ({filtre})", cx))
        {
            compte.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            total = (int)(long)(await compte.ExecuteScalarAsync(ct))!;
        }
        await using var cmd = new NpgsqlCommand($"""
            SELECT v.id, v.numero, v.date_vente, trim(p.nom || ' ' || COALESCE(p.prenom, '')), v.chifa_facture, v.chifa_rattache_le, r.nom_complet,
                   COALESCE(v.chifa_ecart_motif, 'Lignes concordantes'), v.chifa_ecart_detail, v.chifa_ecart_part_organisme,
                   g.sort, g.montant_patient, g.montant_dette_diminuee, g.montant_a_rendre, g.montant_pharmacie,
                   v.patient_id, CASE WHEN g.sort = 'avoir_patient' AND g.statut = 'active' THEN g.montant_a_rendre ELSE 0 END,
                   -- Trop-payé rendu par le tiroir (script 099) : bon actif, non corrigé
                   (SELECT t.numero FROM mouvement_tiroir t WHERE t.regularisation_id = g.id AND t.corrige_id IS NULL
                      AND NOT EXISTS (SELECT 1 FROM mouvement_tiroir c WHERE c.corrige_id = t.id) ORDER BY t.id DESC LIMIT 1)
            {Base} AND ({filtre})
            ORDER BY v.chifa_rattache_le DESC NULLS LAST, v.id DESC LIMIT $2 OFFSET $3
            """, cx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var liste = new List<VoletAvecEcart>();
        await using var rd = await cmd.ExecuteReaderAsync(ct);
        while (await rd.ReadAsync(ct))
            liste.Add(new VoletAvecEcart(rd.GetInt64(0), rd.GetString(1), rd.GetDateTime(2), rd.IsDBNull(3) ? null : rd.GetString(3), rd.GetString(4),
                rd.IsDBNull(5) ? null : rd.GetDateTime(5), rd.IsDBNull(6) ? null : rd.GetString(6), rd.GetString(7),
                rd.IsDBNull(8) ? null : rd.GetString(8), rd.IsDBNull(9) ? null : rd.GetDecimal(9),
                rd.IsDBNull(10) ? null : RegularisationPartOrganisme.Libelle(rd.GetString(10), rd.GetDecimal(11), rd.GetDecimal(12), rd.GetDecimal(13), rd.GetDecimal(14)),
                rd.IsDBNull(15) ? null : rd.GetInt64(15), rd.IsDBNull(16) ? 0 : rd.GetDecimal(16), rd.IsDBNull(17) ? null : rd.GetString(17)));
        return (liste, total);
    }

    private static async Task<List<VenteAttenteVolet>> LireAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<VenteAttenteVolet>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new VenteAttenteVolet(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.IsDBNull(3) ? null : r.GetInt64(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetString(10), [],
                r.GetDecimal(11), r.GetDecimal(12), r.IsDBNull(13) ? null : r.GetString(13), r.GetBoolean(14), r.GetDecimal(15)));
        return liste;
    }

    /// <summary>Lignes des ventes (produit, boîtes, prix unitaire appliqué), une requête pour toutes.</summary>
    private static async Task<List<VenteAttenteVolet>> AvecLignesAsync(NpgsqlConnection cx, List<VenteAttenteVolet> ventes, CancellationToken ct)
    {
        if (ventes.Count == 0) return ventes;
        var lignes = ventes.ToDictionary(v => v.Id, _ => new List<LigneVenteRapprochee>());
        await using var cmd = new NpgsqlCommand("""
            SELECT l.vente_id, l.produit_id, pr.nom_commercial, l.quantite, l.prix_unitaire
            FROM ligne_vente l JOIN produit pr ON pr.id = l.produit_id
            WHERE l.vente_id = ANY($1) ORDER BY l.vente_id, l.id
            """, cx);
        cmd.Parameters.AddWithValue(ventes.Select(v => v.Id).ToArray());
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lignes[r.GetInt64(0)].Add(new LigneVenteRapprochee(r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetDecimal(4)));
        return ventes.Select(v => v with { Lignes = lignes[v.Id] }).ToList();
    }
}
