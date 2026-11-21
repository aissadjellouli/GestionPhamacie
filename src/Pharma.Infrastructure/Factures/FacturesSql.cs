using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Factures;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;

namespace Pharma.Infrastructure.Factures;

/// <summary>Factures clients (031_facture_client.sql).</summary>
public sealed class FacturesSql(NpgsqlDataSource db) : IFactures
{
    private const string SelectSource = """
        SELECT f.vente_id, f.numero, f.ligne_vente_id, f.designation, f.taux_tva, f.quantite_nette, f.montant_patient, f.montant_organisme,
               f.montant_remise, f.date_vente, f.patient_id, NULLIF(trim(concat_ws(' ', p.nom, p.prenom)), ''), f.organisme_id,
               f.ordonnance_id, f.ordonnance_numero,
               EXISTS (SELECT 1 FROM facture_client_ligne fl JOIN facture_client fc ON fc.id = fl.facture_id
                       WHERE fl.ligne_vente_id = f.ligne_vente_id AND fl.part = $1 AND fc.type = 'facture'
                         AND fc.statut IN ('emise', 'rejetee') AND fc.id IS DISTINCT FROM $2),
               (SELECT v.montant_don FROM vente v WHERE v.id = f.vente_id),
               NULLIF(trim(p.num_assure), ''),
               (SELECT m.nom FROM ordonnance o JOIN medecin m ON m.id = o.medecin_id WHERE o.id = f.ordonnance_id),
               -- Réductions de la ligne (remise automatique, remise du vendeur, don partiel) au prorata de la quantité nette des retours
               (SELECT round((lv.reduction_auto + lv.reduction_manuelle) * f.quantite_nette / lv.quantite, 2)
                FROM ligne_vente lv WHERE lv.id = f.ligne_vente_id),
               -- Canal de la part organisme (script 089) : chifa / mdn jamais refacturées à l'organisme
               (SELECT v.canal_organisme FROM vente v WHERE v.id = f.vente_id)
        FROM v_ligne_vente_facturable f
        LEFT JOIN patient p ON p.id = f.patient_id
        """;

    public async Task<IReadOnlyList<LigneVenteSource>> CandidatesAsync(string part, long? patientId, long? organismeId, DateOnly du, DateOnly au,
        string? recherche, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            {SelectSource}
            WHERE f.date_vente >= $3::date AND f.date_vente < $4::date + 1
              AND ($5::bigint IS NULL OR f.patient_id = $5)
              AND ($6::bigint IS NULL OR f.organisme_id = $6)
              AND ($1 = 'patient' OR (f.organisme_id IS NOT NULL
                   AND EXISTS (SELECT 1 FROM vente vc WHERE vc.id = f.vente_id AND vc.canal_organisme = 'logiciel')))
              AND ($7::text IS NULL OR f.numero ILIKE '%' || $7 || '%' OR f.ordonnance_numero ILIKE '%' || $7 || '%'
                   OR concat_ws(' ', p.nom, p.prenom) ILIKE '%' || $7 || '%')
            ORDER BY f.date_vente, f.numero, f.ligne_vente_id
            LIMIT 3000
            """);
        cmd.Parameters.AddWithValue(part);
        cmd.Parameters.Add(Long(null));
        cmd.Parameters.AddWithValue(du);
        cmd.Parameters.AddWithValue(au);
        cmd.Parameters.Add(Long(patientId));
        cmd.Parameters.Add(Long(organismeId));
        cmd.Parameters.Add(Texte(recherche));
        return await LireSources(cmd, ct);
    }

    public async Task<IReadOnlyList<LigneVenteSource>> LignesDesVentesAsync(IReadOnlyList<long> ventes, string part, long? sauf, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{SelectSource} WHERE f.vente_id = ANY($3) ORDER BY f.numero, f.ligne_vente_id");
        cmd.Parameters.AddWithValue(part);
        cmd.Parameters.Add(Long(sauf));
        cmd.Parameters.AddWithValue(ventes.ToArray());
        return await LireSources(cmd, ct);
    }

    private static async Task<IReadOnlyList<LigneVenteSource>> LireSources(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<LigneVenteSource>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new LigneVenteSource(
                new LigneVendue(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6),
                    r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(16), r.IsDBNull(19) ? 0 : r.GetDecimal(19)),
                r.GetDateTime(9), Nl(r, 10), Txt(r, 11), Nl(r, 12), Nl(r, 13), Txt(r, 14), r.GetBoolean(15), Txt(r, 17), Txt(r, 18),
                Txt(r, 20) ?? Pharma.Application.Caisse.CanalOrganisme.Logiciel));
        return liste;
    }

    public async Task<decimal> PayeAuComptoirAsync(IReadOnlyList<long> ventes, CancellationToken ct = default)
    {
        // Retours : part du patient seulement (la part rendue d'une régularisation « à la charge du patient » n'a pas été payée au comptoir, script 158)
        await using var cmd = db.CreateCommand($"""
            SELECT COALESCE(SUM(GREATEST(LEAST(
                     (SELECT COALESCE(SUM(x.montant), 0) FROM paiement x WHERE x.vente_id = v.id AND x.mode <> 'credit')
                   - {Pharma.Infrastructure.Caisse.AnnulationsVenteSql.DejaRenduPartPatient},
                   v.part_patient), 0)), 0)
            FROM vente v WHERE v.id = ANY($1)
            """);
        cmd.Parameters.AddWithValue(ventes.ToArray());
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<(string Nom, string? Infos)?> DestinataireAsync(Destinataire d, long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(d switch
        {
            Destinataire.Patient => """
                SELECT trim(concat_ws(' ', nom, prenom)), NULLIF(concat_ws(' · ', 'N° d’assuré ' || num_assure, 'Tél. ' || telephone), '')
                FROM patient WHERE id = $1
                """,
            Destinataire.Entreprise => """
                SELECT raison_sociale, NULLIF(concat_ws(E'\n', adresse,
                         NULLIF(concat_ws(' · ', 'RC ' || rc, 'NIF ' || nif, 'NIS ' || nis, 'AI ' || article_imposition), ''), 'Tél. ' || telephone), '')
                FROM client_facture WHERE id = $1 AND actif
                """,
            _ => """
                SELECT nom || ' (' || code || ')', NULLIF(concat_ws(E'\n', adresse, 'NIF ' || nif), '')
                FROM organisme WHERE id = $1 AND actif
                """
        });
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetString(0), Txt(r, 1)) : null;
    }

    public async Task<(long Id, string Numero)> EmettreAsync(FactureAEcrire f, long? remplaceId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await VerrouillerEtControler(cx, tx, f, remplaceId, ct);
        if (f.Type == "avoir" && f.FactureOrigineId is { } factureAvoir)
            await ControlerAvoirSousVerrouAsync(cx, tx, factureAvoir, f.Totaux.Ttc, f.Motif, ct);

        int sequence;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO facture_compteur (type, annee, dernier) VALUES ($1, $2, 1)
            ON CONFLICT (type, annee) DO UPDATE SET dernier = facture_compteur.dernier + 1
            RETURNING dernier
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(f.Type);
            cmd.Parameters.AddWithValue(f.Date.Year);
            sequence = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var numero = RegleFacture.Numero(f.Type == "avoir", f.Date.Year, sequence);

        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO facture_client (type, numero, date_facture, destinataire, patient_id, client_id, organisme_id, destinataire_nom, destinataire_infos,
                                        periode_du, periode_au, montant_ht, montant_tva, montant_ttc, deja_paye, remplace_id, facture_origine_id, motif,
                                        commentaire, utilisateur_id, date_echeance)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(f.Type);
            cmd.Parameters.AddWithValue(numero);
            cmd.Parameters.AddWithValue(f.Date);
            cmd.Parameters.AddWithValue(f.Destinataire.Code());
            cmd.Parameters.Add(Long(f.PatientId));
            cmd.Parameters.Add(Long(f.ClientId));
            cmd.Parameters.Add(Long(f.OrganismeId));
            cmd.Parameters.AddWithValue(f.DestinataireNom);
            cmd.Parameters.Add(Texte(f.DestinataireInfos));
            cmd.Parameters.Add(Date(f.Du));
            cmd.Parameters.Add(Date(f.Au));
            cmd.Parameters.AddWithValue(f.Totaux.Ht);
            cmd.Parameters.AddWithValue(f.Totaux.Tva);
            cmd.Parameters.AddWithValue(f.Totaux.Ttc);
            cmd.Parameters.AddWithValue(f.DejaPaye);
            cmd.Parameters.Add(Long(remplaceId));
            cmd.Parameters.Add(Long(f.FactureOrigineId));
            cmd.Parameters.Add(Texte(f.Motif));
            cmd.Parameters.Add(Texte(f.Commentaire));
            cmd.Parameters.AddWithValue(f.UtilisateurId);
            cmd.Parameters.Add(Date(f.Echeance));   // date d'échéance (040) ; null pour un avoir
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await EcrireLignes(cx, tx, id, f.Lignes, ct);
        if (f.Type == "avoir" && f.FactureOrigineId is { } origine)
            await DiminuerDetteParAvoirAsync(cx, tx, id, numero, origine, f.Totaux.Ttc, f.UtilisateurId, ct);

        if (remplaceId is { } ancienne)
            await using (var cmd = new NpgsqlCommand(
                "UPDATE facture_client SET statut = 'remplacee', remplacee_par = $2 WHERE id = $1 AND statut = 'rejetee'", cx, tx))
            {
                cmd.Parameters.AddWithValue(ancienne);
                cmd.Parameters.AddWithValue(id);
                if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La facture à remplacer n’est plus « rejetée » : rechargez-la.");
            }
        await tx.CommitAsync(ct);
        return (id, numero);
    }

    /// <summary>
    /// Avoir : facture d'origine verrouillée (FOR UPDATE) et contrôle refait dans la transaction (2026-11-15) : deux avoirs émis
    /// en même temps ne dépassent jamais ensemble le montant de la facture.
    /// </summary>
    private static async Task ControlerAvoirSousVerrouAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long factureId, decimal montant, string? motif,
        CancellationToken ct)
    {
        string type, statut;
        decimal ttc;
        await using (var cmd = new NpgsqlCommand("SELECT type, statut, montant_ttc FROM facture_client WHERE id = $1 FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(factureId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Facture introuvable.");
            (type, statut, ttc) = (r.GetString(0), r.GetString(1), r.GetDecimal(2));
        }
        decimal avoirs;
        await using (var cmd = new NpgsqlCommand(
            "SELECT COALESCE(sum(montant_ttc), 0) FROM facture_client WHERE facture_origine_id = $1 AND type = 'avoir'", cx, tx))
        {
            cmd.Parameters.AddWithValue(factureId);
            avoirs = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        }
        RegleFacture.VerifierAvoir(type, statut, ttc, avoirs, montant, motif);
    }

    /// <summary>
    /// Avoir sur une facture dont des ventes sont à crédit (084_avoir_dette_patient.sql) : la dette du patient diminue de
    /// min(avoir, reste dû à crédit de ces ventes), mouvement « correction » rattaché à la vente, même transaction que l'avoir.
    /// La part imputée sur la dette est gardée dans facture_client.part_credit de l'avoir : v_facture_client ne la retire
    /// qu'une fois du reste de la facture (elle y apparaît déjà comme réglée sur les ventes à crédit).
    /// </summary>
    private static async Task DiminuerDetteParAvoirAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long avoirId, string numeroAvoir, long factureId,
        decimal montant, long utilisateurId, CancellationToken ct)
    {
        // Patients verrouillés : un remboursement ou un encaissement simultané ne diminue pas deux fois la même dette
        await using (var verrou = new NpgsqlCommand("""
            SELECT 1 FROM patient WHERE id IN (
                SELECT v.patient_id FROM facture_client_ligne fl JOIN vente v ON v.id = fl.vente_id
                WHERE fl.facture_id = $1 AND fl.part = 'patient' AND v.patient_id IS NOT NULL)
            ORDER BY id FOR UPDATE
            """, cx, tx))
        {
            verrou.Parameters.AddWithValue(factureId);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        var restes = new List<(long VenteId, decimal Reste)>();
        var patients = new Dictionary<long, long>();
        string numeroFacture;
        await using (var cmd = new NpgsqlCommand("SELECT numero FROM facture_client WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(factureId);
            numeroFacture = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var cmd = new NpgsqlCommand("""
            SELECT c.vente_id, c.patient_id, c.reste
            FROM v_vente_credit c
            WHERE c.reste > 0 AND c.vente_id IN (SELECT fl.vente_id FROM facture_client_ligne fl
                                                 WHERE fl.facture_id = $1 AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)
            ORDER BY c.date_vente, c.vente_id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(factureId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                restes.Add((r.GetInt64(0), r.GetDecimal(2)));
                patients[r.GetInt64(0)] = r.GetInt64(1);
            }
        }
        var parts = RegleFacture.RepartirAvoirSurDette(montant, restes);
        if (parts.Count == 0) return;
        foreach (var (vente, part) in parts)
        {
            await Caisse.ReleveCredit.ContexteAsync(cx, tx, Caisse.ReleveCredit.Correction, vente, utilisateurId, null,
                $"Avoir {numeroAvoir} sur facture {numeroFacture}", ct);
            await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx);
            cmd.Parameters.AddWithValue(patients[vente]);
            cmd.Parameters.AddWithValue(part);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient de cette facture vient de changer : rechargez la facture.");
            // 087 : la baisse de dette solde CETTE vente de la facture, jamais une dette plus ancienne
            await Caisse.ReleveCredit.ImputerAsync(cx, tx, vente, part, ct);
        }
        await using (var cmd = new NpgsqlCommand("UPDATE facture_client SET part_credit = $2 WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(avoirId);
            cmd.Parameters.AddWithValue(parts.Sum(p => p.Montant));
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Une émission à la fois : une ligne de vente n'est facturée qu'une fois par part (hors facture corrigée ou remplacée).</summary>
    private static async Task VerrouillerEtControler(NpgsqlConnection cx, NpgsqlTransaction tx, FactureAEcrire f, long? sauf, CancellationToken ct)
    {
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('facture_client'))", cx, tx))
            await verrou.ExecuteNonQueryAsync(ct);
        var lignes = f.Lignes.Where(l => l.LigneVenteId is not null).ToList();
        if (lignes.Count == 0) return;
        await using var cmd = new NpgsqlCommand("""
            SELECT v.numero FROM facture_client_ligne fl
            JOIN facture_client fc ON fc.id = fl.facture_id
            JOIN vente v ON v.id = fl.vente_id
            WHERE fl.ligne_vente_id = ANY($1) AND fl.part = $2 AND fc.type = 'facture' AND fc.statut IN ('emise', 'rejetee')
              AND fc.id IS DISTINCT FROM $3
            LIMIT 1
            """, cx, tx);
        cmd.Parameters.AddWithValue(lignes.Select(l => l.LigneVenteId!.Value).ToArray());
        cmd.Parameters.AddWithValue(lignes[0].Part);
        cmd.Parameters.Add(Long(sauf));
        if (await cmd.ExecuteScalarAsync(ct) is string vente)
            throw new RegleMetierException($"La vente {vente} vient d’être facturée pour cette part.");
    }

    private static async Task EcrireLignes(NpgsqlConnection cx, NpgsqlTransaction tx, long factureId, IReadOnlyList<LigneFacture> lignes, CancellationToken ct)
    {
        var ordre = 0;
        foreach (var l in lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO facture_client_ligne (facture_id, ordre, vente_id, ligne_vente_id, part, designation, quantite, prix_unitaire_ttc, taux_tva, montant_ttc)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """, cx, tx);
            cmd.Parameters.AddWithValue(factureId);
            cmd.Parameters.AddWithValue(++ordre);
            cmd.Parameters.Add(Long(l.VenteId));
            cmd.Parameters.Add(Long(l.LigneVenteId));
            cmd.Parameters.AddWithValue(l.Part);
            cmd.Parameters.AddWithValue(l.Designation);
            cmd.Parameters.AddWithValue(l.Quantite);
            cmd.Parameters.AddWithValue(l.PrixUnitaireTtc);
            cmd.Parameters.AddWithValue(l.TauxTva);
            cmd.Parameters.AddWithValue(l.MontantTtc);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    private const string SelectResume = """
        SELECT id, type, numero, date_facture, destinataire, destinataire_nom, montant_ttc, deja_paye, avoirs, regle, reste, statut, version,
               hors_suivi, motif_hors_suivi, remplace_numero, remplacee_par_numero, origine_numero, motif_rejet, organisme_id,
               patient_id, client_id, destinataire_infos, periode_du, periode_au, montant_ht, montant_tva, commentaire, motif, employe,
               -- Échéance lue dans la table : la vue garde les colonnes de 031 pour rester rejouable (040)
               (SELECT fe.date_echeance FROM facture_client fe WHERE fe.id = v_facture_client.id) AS date_echeance,
               utilisateur_id
        FROM v_facture_client
        """;

    /// <summary>Filtres de la liste (paramètres $1 à $5 : recherche, destinataire, suivi, du, au).</summary>
    private const string OuFiltres = """
        WHERE ($1::text IS NULL OR numero ILIKE '%' || $1 || '%' OR destinataire_nom ILIKE '%' || $1 || '%')
          AND ($2::text IS NULL OR destinataire = $2)
          AND ($3::text IS NULL
               OR ($3 = 'a_encaisser' AND type = 'facture' AND statut <> 'remplacee' AND NOT hors_suivi AND reste > 0)
               OR ($3 = 'echues' AND type = 'facture' AND statut <> 'remplacee' AND NOT hors_suivi AND reste > 0
                   AND (SELECT fe.date_echeance FROM facture_client fe WHERE fe.id = v_facture_client.id) < CURRENT_DATE)
               OR ($3 = 'soldees' AND type = 'facture' AND statut <> 'remplacee' AND reste = 0)
               OR ($3 = 'rejetees' AND statut = 'rejetee' AND reste > 0)
               OR ($3 = 'hors_suivi' AND hors_suivi)
               OR ($3 = 'avoirs' AND type = 'avoir'))
          AND ($4::date IS NULL OR date_facture >= $4)
          AND ($5::date IS NULL OR date_facture <= $5)
        """;

    private static void Filtres(NpgsqlCommand cmd, FiltreFactures filtre)
    {
        cmd.Parameters.Add(Texte(string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim()));
        cmd.Parameters.Add(Texte(filtre.Destinataire));
        cmd.Parameters.Add(Texte(filtre.Suivi));
        cmd.Parameters.Add(Date(filtre.Du));
        cmd.Parameters.Add(Date(filtre.Au));
    }

    /// <summary>Liste sans pagination, pour les traitements (factures à encaisser de la trésorerie) : 300 lignes au plus.</summary>
    public async Task<IReadOnlyList<FactureResume>> ListerAsync(FiltreFactures filtre, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            {SelectResume}
            {OuFiltres}
            ORDER BY date_facture DESC, id DESC
            LIMIT 300
            """);
        Filtres(cmd, filtre);
        var liste = new List<FactureResume>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Resume(r));
        return liste;
    }

    /// <summary>Une page de la liste, et les totaux de toutes les factures qui répondent aux filtres (pas seulement la page).</summary>
    public async Task<ListeFactures> PageAsync(FiltreFactures filtre, Pharma.Application.Commun.DemandePage page, CancellationToken ct = default)
    {
        Pharma.Application.Commun.Page<FactureResume> resultat;
        await using (var cmd = db.CreateCommand($"""
            {SelectResume.Replace("FROM v_facture_client", ", " + Lecture.PaginationSql.Total + " FROM v_facture_client")}
            {OuFiltres}
            ORDER BY date_facture DESC, id DESC
            {Lecture.PaginationSql.Limite(6)}
            """))
        {
            Filtres(cmd, filtre);
            Lecture.PaginationSql.Parametres(cmd, page);
            resultat = await Lecture.PaginationSql.LireAsync(cmd, page, 32, Resume, ct);
        }

        await using var totaux = db.CreateCommand($"""
            SELECT COALESCE(SUM(montant_ttc) FILTER (WHERE type = 'facture' AND statut <> 'remplacee'), 0),
                   COALESCE(SUM(reste) FILTER (WHERE type = 'facture' AND statut <> 'remplacee' AND NOT hors_suivi), 0)
            FROM v_facture_client
            {OuFiltres}
            """);
        Filtres(totaux, filtre);
        await using var r = await totaux.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ListeFactures(resultat, r.GetDecimal(0), r.GetDecimal(1));
    }

    private static FactureResume Resume(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.GetString(2), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetString(4), r.GetString(5), r.GetDecimal(6),
        r.GetDecimal(7), r.GetDecimal(8), r.GetDecimal(9), r.GetDecimal(10), r.GetString(11), r.GetInt32(12), r.GetBoolean(13), Txt(r, 14),
        Txt(r, 15), Txt(r, 16), Txt(r, 17), Txt(r, 18), Nl(r, 19), r.IsDBNull(30) ? null : DateOnly.FromDateTime(r.GetDateTime(30)),
        Nl(r, 20), Nl(r, 21));

    public async Task<IReadOnlyList<EvenementFacture>> HistoriqueAsync(long id, CancellationToken ct = default)
    {
        // Émission (ou remplacement), rejets, corrections, suivi : table et journal ; remplacement, avoirs, règlements : leurs lignes
        await using var cmd = db.CreateCommand("""
            SELECT date, libelle, detail, employe, employe_id, montant FROM (
                SELECT f.cree_le AS date,
                       CASE WHEN f.type = 'avoir' THEN 'Avoir émis' WHEN r.numero IS NOT NULL THEN 'Émise en remplacement de ' || r.numero ELSE 'Émise' END AS libelle,
                       NULL::text AS detail, u.nom_complet AS employe, f.utilisateur_id AS employe_id, f.montant_ttc AS montant
                FROM facture_client f LEFT JOIN facture_client r ON r.id = f.remplace_id LEFT JOIN utilisateur u ON u.id = f.utilisateur_id
                WHERE f.id = $1
                UNION ALL
                SELECT j.cree_le,
                       CASE j.action WHEN 'facture_rejetee' THEN 'Rejetée' WHEN 'facture_rejet_leve' THEN 'Rejet levé'
                                     WHEN 'facture_corrigee' THEN 'Corrigée (nouvelle version, l''ancienne est archivée)'
                                     WHEN 'facture_hors_suivi' THEN 'Retirée du suivi de trésorerie' ELSE 'Remise dans le suivi de trésorerie' END,
                       j.details->>'motif', u.nom_complet, j.utilisateur_id, NULL
                FROM journal_audit j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
                WHERE j.table_cible = 'facture_client' AND j.enregistrement_id = $1
                  AND j.action IN ('facture_rejetee', 'facture_rejet_leve', 'facture_corrigee', 'facture_hors_suivi', 'facture_remise_en_suivi')
                UNION ALL
                SELECT n.cree_le, 'Remplacée par ' || n.numero, NULL, u.nom_complet, n.utilisateur_id, n.montant_ttc
                FROM facture_client n LEFT JOIN utilisateur u ON u.id = n.utilisateur_id WHERE n.remplace_id = $1
                UNION ALL
                SELECT a.cree_le, 'Avoir ' || a.numero, a.motif, u.nom_complet, a.utilisateur_id, a.montant_ttc
                FROM facture_client a LEFT JOIN utilisateur u ON u.id = a.utilisateur_id WHERE a.facture_origine_id = $1 AND a.type = 'avoir'
                UNION ALL
                SELECT m.cree_le, CASE m.nature WHEN 'versement_organisme' THEN 'Payée par l''organisme' ELSE 'Règlement reçu' END,
                       NULLIF(concat_ws(' · ', c.nom, m.reference), ''), u.nom_complet, m.utilisateur_id, rf.montant
                FROM reglement_facture_client rf JOIN mouvement_tresorerie m ON m.id = rf.mouvement_id
                JOIN compte_tresorerie c ON c.id = m.compte_id LEFT JOIN utilisateur u ON u.id = m.utilisateur_id
                WHERE rf.facture_id = $1
            ) h
            ORDER BY date
            """);
        cmd.Parameters.AddWithValue(id);
        var liste = new List<EvenementFacture>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new EvenementFacture(r.GetDateTime(0), r.GetString(1), Txt(r, 2), Txt(r, 3), Nl(r, 4), r.IsDBNull(5) ? null : r.GetDecimal(5)));
        return liste;
    }

    public async Task<FactureDetail?> DetailAsync(long id, int? version, CancellationToken ct = default)
    {
        FactureDetail detail;
        await using (var cmd = db.CreateCommand($"{SelectResume} WHERE id = $1"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            detail = new FactureDetail(Resume(r), Nl(r, 20), Nl(r, 21), Txt(r, 22), D(r, 23), D(r, 24), r.GetDecimal(25), r.GetDecimal(26),
                Txt(r, 27), Txt(r, 28), r.GetString(29), [], [], Nl(r, 31));
        }

        // Ventes à crédit de la facture (part du patient) réglées depuis par le patient sur sa dette : même calcul que v_facture_client.
        // La part des avoirs imputée sur la dette (part_credit) y figure aussi : elle est retirée, déjà affichée dans « Avoirs » (2026-11-15)
        await using (var cmd = db.CreateCommand("""
            SELECT (SELECT COALESCE(sum(c.regle), 0) FROM v_vente_credit c
                    WHERE c.vente_id IN (SELECT fl.vente_id FROM facture_client_ligne fl WHERE fl.facture_id = $1 AND fl.part = 'patient' AND fl.vente_id IS NOT NULL)),
                   (SELECT COALESCE(sum(a.part_credit), 0) FROM facture_client a WHERE a.facture_origine_id = $1 AND a.type = 'avoir')
            """))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            detail = detail with { RegleParDette = RegleFacture.RegleParDetteAffiche(r.GetDecimal(0), r.GetDecimal(1)) };
        }

        var versions = new List<int>();
        await using (var cmd = db.CreateCommand("SELECT version FROM facture_client_version WHERE facture_id = $1 ORDER BY version"))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) versions.Add(r.GetInt32(0));
        }

        if (version is { } v && v != detail.Resume.Version)
        {
            // Version précédente : contenu figé au moment de la correction
            await using var cmd = db.CreateCommand("SELECT contenu FROM facture_client_version WHERE facture_id = $1 AND version = $2");
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(v);
            if (await cmd.ExecuteScalarAsync(ct) is not string json) throw new RegleMetierException("Version introuvable.");
            using var doc = JsonDocument.Parse(json);
            var c = doc.RootElement;
            var lignesV = c.GetProperty("lignes").EnumerateArray().Select(l => new LigneFacture(
                Opt(l, "vente_id"), Opt(l, "ligne_vente_id"), l.GetProperty("part").GetString()!, l.GetProperty("designation").GetString()!,
                l.GetProperty("quantite").GetDecimal(), l.GetProperty("prix_unitaire_ttc").GetDecimal(), l.GetProperty("taux_tva").GetDecimal(),
                l.GetProperty("montant_ttc").GetDecimal())).ToList();
            return detail with
            {
                Resume = detail.Resume with
                {
                    Version = v, Statut = "rejetee", Ttc = c.GetProperty("montant_ttc").GetDecimal(), DejaPaye = c.GetProperty("deja_paye").GetDecimal(),
                    Date = DateOnly.Parse(c.GetProperty("date_facture").GetString()!), MotifRejet = c.TryGetProperty("motif_rejet", out var m) ? m.GetString() : null
                    ,
                    Echeance = c.TryGetProperty("date_echeance", out var ech) && ech.ValueKind == JsonValueKind.String ? DateOnly.Parse(ech.GetString()!) : detail.Resume.Echeance
                },
                Ht = c.GetProperty("montant_ht").GetDecimal(), Tva = c.GetProperty("montant_tva").GetDecimal(),
                Commentaire = c.TryGetProperty("commentaire", out var cm) ? cm.GetString() : null, Lignes = lignesV, Versions = versions
            };
        }

        var lignes = new List<LigneFacture>();
        await using (var cmd = db.CreateCommand("""
            SELECT vente_id, ligne_vente_id, part, designation, quantite, prix_unitaire_ttc, taux_tva, montant_ttc
            FROM facture_client_ligne WHERE facture_id = $1 ORDER BY ordre
            """))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneFacture(Nl(r, 0), Nl(r, 1), r.GetString(2), r.GetString(3), r.GetDecimal(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7)));
        }
        return detail with { Lignes = lignes, Versions = versions };
    }

    public async Task RejeterAsync(long id, string motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE facture_client SET statut = 'rejetee', motif_rejet = $2 WHERE id = $1 AND statut = 'emise' AND type = 'facture'");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(motif);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La facture n’est plus « émise » : rechargez-la.");
    }

    public async Task LeverRejetAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE facture_client SET statut = 'emise', motif_rejet = NULL WHERE id = $1 AND statut = 'rejetee' AND type = 'facture'");
        cmd.Parameters.AddWithValue(id);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La facture n’est plus « rejetée » : rechargez-la.");
    }

    public async Task CorrigerAsync(long id, FactureAEcrire f, string? motif, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await VerrouillerEtControler(cx, tx, f, id, ct);

        // La version en vigueur part dans l'historique, avec le motif du rejet
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO facture_client_version (facture_id, version, contenu, motif, utilisateur_id)
            SELECT f.id, f.version,
                   jsonb_build_object('date_facture', f.date_facture, 'date_echeance', f.date_echeance, 'montant_ht', f.montant_ht, 'montant_tva', f.montant_tva, 'montant_ttc', f.montant_ttc,
                                      'deja_paye', f.deja_paye, 'commentaire', f.commentaire, 'motif_rejet', f.motif_rejet,
                                      'lignes', COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.ordre) FROM facture_client_ligne l WHERE l.facture_id = f.id), '[]'::jsonb)),
                   COALESCE($2, f.motif_rejet), $3
            FROM facture_client f WHERE f.id = $1 AND f.statut = 'rejetee'
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.Add(Texte(motif));
            cmd.Parameters.AddWithValue(f.UtilisateurId);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La facture n’est plus « rejetée » : rechargez-la.");
        }
        await using (var cmd = new NpgsqlCommand("DELETE FROM facture_client_ligne WHERE facture_id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await EcrireLignes(cx, tx, id, f.Lignes, ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE facture_client SET date_facture = $2, periode_du = $3, periode_au = $4, montant_ht = $5, montant_tva = $6, montant_ttc = $7,
                   deja_paye = $8, commentaire = $9, date_echeance = $10, version = version + 1, statut = 'emise', motif_rejet = NULL
            WHERE id = $1
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(f.Date);
            cmd.Parameters.Add(Date(f.Du));
            cmd.Parameters.Add(Date(f.Au));
            cmd.Parameters.AddWithValue(f.Totaux.Ht);
            cmd.Parameters.AddWithValue(f.Totaux.Tva);
            cmd.Parameters.AddWithValue(f.Totaux.Ttc);
            cmd.Parameters.AddWithValue(f.DejaPaye);
            cmd.Parameters.Add(Texte(f.Commentaire));
            cmd.Parameters.Add(Date(f.Echeance));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task HorsSuiviAsync(long id, bool horsSuivi, string? motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE facture_client SET hors_suivi = $2, motif_hors_suivi = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(horsSuivi);
        cmd.Parameters.Add(Texte(motif));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<int> DelaiPaiementAsync(Destinataire d, long id, CancellationToken ct = default)
    {
        if (d == Destinataire.Patient) return RegleFacture.DelaiEcheanceJours;
        await using var cmd = db.CreateCommand(d == Destinataire.Entreprise
            ? "SELECT delai_paiement_jours FROM client_facture WHERE id = $1"
            : "SELECT delai_paiement_jours FROM organisme WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteScalarAsync(ct) is int jours ? jours : RegleFacture.DelaiEcheanceJours;
    }

    public async Task<IReadOnlyList<ClientFacture>> ClientsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, raison_sociale, adresse, telephone, nif, rc, nis, article_imposition, actif, email, delai_paiement_jours FROM client_facture ORDER BY NOT actif, raison_sociale");
        var liste = new List<ClientFacture>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ClientFacture(r.GetInt64(0), r.GetString(1), Txt(r, 2), Txt(r, 3), Txt(r, 4), Txt(r, 5), Txt(r, 6), Txt(r, 7), r.GetBoolean(8), Txt(r, 9), r.GetInt32(10)));
        return liste;
    }

    public async Task<long> EnregistrerClientAsync(ClientFacture c, CancellationToken ct = default)
    {
        // Doublon selon les majuscules et les espaces autour (« SARL Alpha » et « sarl alpha ») : refusé avec un message clair
        // (l'index unique sur lower(btrim(raison_sociale)) du script 077 couvre deux postes simultanés)
        await using (var doublon = db.CreateCommand(
            "SELECT raison_sociale FROM client_facture WHERE lower(btrim(raison_sociale)) = lower(btrim($1)) AND id IS DISTINCT FROM $2 LIMIT 1"))
        {
            doublon.Parameters.AddWithValue(c.RaisonSociale);
            doublon.Parameters.Add(Long(c.Id));
            if (await doublon.ExecuteScalarAsync(ct) is string existant)
                throw new RegleMetierException($"Un client s’appelle déjà « {existant} » (majuscules et espaces ne comptent pas).", "raison_sociale");
        }
        await using var cmd = db.CreateCommand(c.Id is null
            ? "INSERT INTO client_facture (raison_sociale, adresse, telephone, nif, rc, nis, article_imposition, actif, email, delai_paiement_jours) VALUES ($2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id"
            : """
              UPDATE client_facture SET raison_sociale = $2, adresse = $3, telephone = $4, nif = $5, rc = $6, nis = $7, article_imposition = $8, actif = $9, email = $10,
                                        delai_paiement_jours = $11
              WHERE id = $1 RETURNING id
              """);
        cmd.Parameters.Add(Long(c.Id));
        cmd.Parameters.AddWithValue(c.RaisonSociale);
        foreach (var v in new[] { c.Adresse, c.Telephone, c.Nif, c.Rc, c.Nis, c.ArticleImposition }) cmd.Parameters.Add(Texte(v));
        cmd.Parameters.AddWithValue(c.Actif);
        cmd.Parameters.Add(Texte(c.Email));
        cmd.Parameters.AddWithValue(c.DelaiPaiementJours);
        try { return (long)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Un client s’appelle déjà « {c.RaisonSociale} ».");
        }
    }

    public async Task<IReadOnlyList<OrganismeFiche>> OrganismesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, code, nom, adresse, nif, actif, telephone, email, delai_paiement_jours FROM organisme ORDER BY NOT actif, code");
        var liste = new List<OrganismeFiche>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new OrganismeFiche(r.GetInt64(0), r.GetString(1), r.GetString(2), Txt(r, 3), Txt(r, 4), r.GetBoolean(5), Txt(r, 6), Txt(r, 7), r.GetInt32(8)));
        return liste;
    }

    public async Task<long> EnregistrerOrganismeAsync(OrganismeFiche o, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(o.Id is null
            ? "INSERT INTO organisme (code, nom, adresse, nif, actif, telephone, email, delai_paiement_jours) VALUES ($2, $3, $4, $5, $6, $7, $8, $9) RETURNING id"
            : "UPDATE organisme SET code = $2, nom = $3, adresse = $4, nif = $5, actif = $6, telephone = $7, email = $8, delai_paiement_jours = $9 WHERE id = $1 RETURNING id");
        cmd.Parameters.Add(Long(o.Id));
        cmd.Parameters.AddWithValue(o.Code);
        cmd.Parameters.AddWithValue(o.Nom);
        cmd.Parameters.Add(Texte(o.Adresse));
        cmd.Parameters.Add(Texte(o.Nif));
        cmd.Parameters.AddWithValue(o.Actif);
        cmd.Parameters.Add(Texte(o.Telephone));
        cmd.Parameters.Add(Texte(o.Email));
        cmd.Parameters.AddWithValue(o.DelaiPaiementJours);
        try { return (long)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new RegleMetierException($"Le sigle « {o.Code} » est déjà utilisé.");
        }
    }

    private static long? Nl(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);
    private static string? Txt(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateOnly? D(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : DateOnly.FromDateTime(r.GetDateTime(i));
    private static long? Opt(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static NpgsqlParameter Long(long? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };
    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
    private static NpgsqlParameter Date(DateOnly? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date };
}
