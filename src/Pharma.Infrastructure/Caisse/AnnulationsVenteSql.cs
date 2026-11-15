using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Annulation d'une vente (027_annulation_vente.sql) : tout en une transaction.</summary>
public sealed class AnnulationsVenteSql(NpgsqlDataSource db) : IAnnulationsVente, IVentesParBoite
{
    private const string Select = $"""
        SELECT v.id, v.numero, v.date_vente, u.nom_complet, NULLIF(trim(concat_ws(' ', pa.nom, pa.prenom)), ''),
               v.montant_total, v.part_patient, v.part_organisme,
               (SELECT p.mode FROM paiement p WHERE p.vente_id = v.id ORDER BY p.id LIMIT 1),
               v.statut, COALESCE(s.fermee_le IS NULL, false), v.bordereau_id IS NOT NULL,
               -- Déjà rendu par des retours (argent ou dette diminuée), dette actuelle du patient, facture en vigueur
               (SELECT COALESCE(SUM(rc.montant_rembourse), 0) FROM retour_client rc WHERE rc.vente_id = v.id),
               COALESCE(pa.solde_credit, 0),
               (SELECT fc.numero FROM facture_client_ligne fl JOIN facture_client fc ON fc.id = fl.facture_id
                WHERE fl.vente_id = v.id AND fc.type = 'facture' AND fc.statut IN ('emise', 'rejetee') ORDER BY fc.id LIMIT 1),
               v.utilisateur_id, v.patient_id,
               -- Vente à crédit : remboursements réellement versés (ni correction ni abandon de dette) imputés à cette vente, les plus
               -- anciennes dettes d'abord (ouverture et reprise, puis ventes à crédit nettes de leurs retours et annulations)
               COALESCE((
                   WITH m AS (SELECT * FROM credit_mouvement WHERE patient_id = v.patient_id),
                   dettes AS (
                       SELECT 0 AS rang, min(cree_le) AS le, NULL::bigint AS vente_id, sum(montant) AS net FROM m WHERE type IN ('ouverture', 'reprise')
                       UNION ALL
                       SELECT 1, min(cree_le), vente_id, sum(montant) FROM m
                       WHERE vente_id IS NOT NULL AND type IN ('vente_credit', 'retour', 'annulation') GROUP BY vente_id),
                   cumul AS (SELECT vente_id, GREATEST(net, 0) AS net,
                                    COALESCE(sum(GREATEST(net, 0)) OVER (ORDER BY rang, le, vente_id ROWS BETWEEN UNBOUNDED PRECEDING AND 1 PRECEDING), 0) AS avant
                             FROM dettes WHERE net IS NOT NULL)
                   SELECT LEAST(GREATEST((SELECT COALESCE(-sum(montant), 0) FROM m WHERE type = 'remboursement') - c.avant, 0), c.net)
                   FROM cumul c WHERE c.vente_id = v.id), 0),
               -- « Remis à » / « Pour » (script 091)
               {RemiseVenteSql.RemisA}, {RemiseVenteSql.Pour},
               -- Régularisation de part organisme active (097, 098) ; à la charge du patient : part déjà payée, la part à crédit de la
               -- vente étant réputée payée d'abord (v_vente_credit : crédit accordé de la vente = part à crédit + régularisation)
               g.id, g.sort, g.montant_patient,
               CASE WHEN g.sort = 'charge_patient' THEN COALESCE((
                   SELECT LEAST(g.montant_patient, GREATEST(0, vc.regle - (vc.montant - g.montant_patient)))
                   FROM v_vente_credit vc WHERE vc.vente_id = v.id), 0) ELSE 0 END
        FROM vente v
        JOIN utilisateur u ON u.id = v.utilisateur_id
        LEFT JOIN patient pa ON pa.id = v.patient_id
        LEFT JOIN session_caisse s ON s.id = v.session_caisse_id
        LEFT JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active'
        """;

    public async Task<VenteAnnulable?> TrouverAsync(long? venteId, string? numero, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE ($1::bigint IS NOT NULL AND v.id = $1) OR ($2::text IS NOT NULL AND v.numero = $2)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)venteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numero ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return (await LireAsync(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<VenteAnnulable>> DernieresAsync(long sessionCaisseId, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE v.session_caisse_id = $1 AND v.statut IN ('validee', 'annulee') ORDER BY v.id DESC LIMIT $2");
        cmd.Parameters.AddWithValue(sessionCaisseId);
        cmd.Parameters.AddWithValue(limite);
        return await LireAsync(cmd, ct);
    }

    public async Task<Pharma.Application.Commun.Page<VenteAnnulable>> VentesSessionAsync(long sessionCaisseId, Pharma.Application.Commun.DemandePage page,
        CancellationToken ct = default)
    {
        const string Ou = "WHERE v.session_caisse_id = $1 AND v.statut IN ('validee', 'annulee')";
        await using var cmd = db.CreateCommand($"{Select} {Ou} ORDER BY v.id DESC LIMIT $2 OFFSET $3");
        cmd.Parameters.AddWithValue(sessionCaisseId);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = await LireAsync(cmd, ct);
        await using var compte = db.CreateCommand($"SELECT count(*) FROM vente v {Ou}");
        compte.Parameters.AddWithValue(sessionCaisseId);
        var total = Convert.ToInt32(await compte.ExecuteScalarAsync(ct));
        return new(lignes, total, page.Page, page.Taille);
    }

    public async Task<long?> VenteDeLaBoiteAsync(long boiteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.vente_id FROM boite b JOIN ligne_vente l ON l.id = b.ligne_vente_id JOIN vente v ON v.id = l.vente_id
            WHERE b.id = $1 AND b.statut = 'vendue' AND v.statut = 'validee'
            """);
        cmd.Parameters.AddWithValue(boiteId);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : null;
    }

    public async Task<IReadOnlyList<VenteAnnulable>> ParProduitAsync(long produitId, long? lotId, string? numeroLot, DateOnly depuis, long? patientId,
        CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            {Select}
            WHERE v.statut = 'validee' AND v.date_vente >= $2::date
              AND (($5::bigint IS NULL AND v.patient_id IS NULL) OR v.patient_id = $5)
              AND EXISTS (SELECT 1 FROM ligne_vente l JOIN lot lo ON lo.id = l.lot_id
                          WHERE l.vente_id = v.id AND l.produit_id = $1
                            AND ($3::bigint IS NULL OR l.lot_id = $3) AND ($4::text IS NULL OR lo.numero_lot = $4)
                            AND l.quantite > COALESCE((SELECT SUM(r.quantite) FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id), 0))
            ORDER BY v.date_vente DESC LIMIT 50
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(depuis);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)lotId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numeroLot ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)patientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return await LireAsync(cmd, ct);
    }

    public async Task<bool> DejaRendueAsync(long boiteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT type = 'retour_client' FROM boite_evenement
            WHERE boite_id = $1 AND type IN ('vente', 'retour_client') ORDER BY id DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(boiteId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_commercial FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    private async Task<IReadOnlyList<VenteAnnulable>> LireAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var ventes = new List<(long Id, VenteAnnulable V)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                ventes.Add((r.GetInt64(0), new VenteAnnulable(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetBoolean(10), r.GetBoolean(11), [],
                    r.GetDecimal(12), r.GetDecimal(13), r.IsDBNull(14) ? null : r.GetString(14), r.GetInt64(15), r.IsDBNull(16) ? null : r.GetInt64(16), r.GetDecimal(17),
                    RemiseVente.Complement(r.IsDBNull(18) ? null : r.GetString(18), r.IsDBNull(19) ? null : r.GetString(19)),
                    r.IsDBNull(20) ? null : new RegularisationVente(r.GetInt64(20), r.GetString(21), r.GetDecimal(22), r.GetDecimal(23)))));
        if (ventes.Count == 0) return [];

        var lignes = new Dictionary<long, List<LigneVenteAnnulable>>();
        await using (var c = db.CreateCommand("""
            SELECT l.vente_id, p.nom_commercial, l.quantite, l.prix_unitaire * l.quantite, lo.numero_lot
            FROM ligne_vente l JOIN produit p ON p.id = l.produit_id JOIN lot lo ON lo.id = l.lot_id
            WHERE l.vente_id = ANY($1) ORDER BY l.id
            """))
        {
            c.Parameters.AddWithValue(ventes.Select(v => v.Id).ToArray());
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!lignes.TryGetValue(r.GetInt64(0), out var l)) lignes[r.GetInt64(0)] = l = [];
                l.Add(new LigneVenteAnnulable(r.GetString(1), r.GetInt32(2), r.GetDecimal(3), r.GetString(4)));
            }
        }
        return ventes.Select(v => v.V with { Lignes = lignes.GetValueOrDefault(v.Id) ?? [] }).ToList();
    }

    /// <summary>Quantité encore chez le patient par lot : vendue moins déjà rendue (retours clients).</summary>
    private const string RestantParLot = """
        SELECT l.lot_id, SUM(l.quantite - COALESCE((SELECT SUM(r.quantite) FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id), 0))::int AS q
        FROM ligne_vente l WHERE l.vente_id = $1 GROUP BY l.lot_id
        HAVING SUM(l.quantite - COALESCE((SELECT SUM(r.quantite) FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id), 0)) > 0
        """;

    public async Task<int> AnnulationsSessionAsync(long sessionCaisseId, CancellationToken ct = default)
    {
        // Annulations faites depuis cette caisse (la vente annulée peut venir d'une caisse plus ancienne)
        await using var cmd = db.CreateCommand("SELECT count(*)::int FROM annulation_vente WHERE session_caisse_id = $1");
        cmd.Parameters.AddWithValue(sessionCaisseId);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<MouvementTiroirEcrit?> AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // 1. La vente passe à « annulée » (une seule fois, même si deux postes annulent en même temps)
        string numero;
        long? patientId;
        await using (var cmd = new NpgsqlCommand(
            "UPDATE vente SET statut = 'annulee' WHERE id = $1 AND statut = 'validee' RETURNING numero, patient_id", cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Cette vente vient d’être annulée ou n’est plus encaissée.");
            (numero, patientId) = (r.GetString(0), r.IsDBNull(1) ? null : r.GetInt64(1));
        }
        // Vente verrouillée : un retour enregistré entre la lecture et l'annulation changerait le montant à rendre (double remboursement)
        if (a.DejaRenduLu is { } lu)
            await using (var cmd = new NpgsqlCommand("SELECT COALESCE(SUM(montant_rembourse), 0) FROM retour_client WHERE vente_id = $1", cx, tx))
            {
                cmd.Parameters.AddWithValue(a.VenteId);
                if ((decimal)(await cmd.ExecuteScalarAsync(ct))! != lu)
                    throw new RegleMetierException("Un retour vient d’être enregistré sur cette vente : rechargez-la avant de l’annuler.");
            }
        var reference = $"Annulation {numero}";

        // 2. Stock remis sur le même lot, sauf les boîtes déjà rendues par un retour client (déjà remises en stock ou détruites) ;
        //    le mouvement « retour_client » inscrit les psychotropes au registre (024)
        await using (var cmd = new NpgsqlCommand($"""
            WITH lignes AS ({RestantParLot}),
                 maj AS (UPDATE lot l SET quantite_disponible = l.quantite_disponible + lignes.q FROM lignes WHERE l.id = lignes.lot_id RETURNING l.id)
            SELECT count(*) FROM maj
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            await cmd.ExecuteScalarAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
            SELECT lot_id, 'retour_client', q, $2, $3 FROM ({RestantParLot}) lignes
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(reference);
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 3. Boîtes identifiées : retour inscrit à leur historique, puis remises en stock
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO boite_evenement (boite_id, type, ligne_vente_id, patient_id, utilisateur_id, commentaire)
            SELECT b.id, 'retour_client', b.ligne_vente_id, $3, $2, $4
            FROM boite b JOIN ligne_vente l ON l.id = b.ligne_vente_id WHERE l.vente_id = $1
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)patientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue($"{reference} : {a.Motif}");
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            UPDATE boite b SET statut = 'en_stock', ligne_vente_id = NULL
            FROM ligne_vente l WHERE l.id = b.ligne_vente_id AND l.vente_id = $1
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 4. Dette diminuée (vente à crédit, ou remboursement retiré de la dette au choix du caissier) ; si elle a bougé entre-temps, on refuse
        if (a.ReductionDette > 0 && (a.PatientDetteId ?? patientId) is { } patient)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Annulation, a.VenteId, a.UtilisateurId, a.Poste,
                $"{reference} : {a.Motif}{(a.AccordClient ? " (déduit de la dette avec l’accord du client)" : "")}", ct);
            await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx);
            cmd.Parameters.AddWithValue(patient);
            cmd.Parameters.AddWithValue(a.ReductionDette);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’annuler.");
        }

        // 4 bis. Régularisation de part organisme (script 098) : jamais supprimée, passée à « annulée » (date, auteur, effet)
        MouvementTiroirEcrit? bon = null;
        if (a.Regularisation is { } g)
            bon = await AnnulerRegularisationAsync(cx, tx, g, a, numero, patientId, ct);

        // 5. Trace de l'annulation et de l'argent rendu (caisse ouverte du poste)
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO annulation_vente (vente_id, motif, utilisateur_id, valide_par, ancienne, mode, montant_rembourse, session_caisse_id, poste)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(a.Motif);
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.ValidePar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(a.Ancienne);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.Mode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(a.MontantRembourse);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(a.Poste);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return bon;
    }

    public async Task<IReadOnlyDictionary<long, decimal>> AutresDettesAsync(long patientId, long venteId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT vente_id, reste FROM v_vente_credit WHERE patient_id = $1 AND vente_id <> $2 AND reste > 0");
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(venteId);
        var d = new Dictionary<long, decimal>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetInt64(0)] = r.GetDecimal(1);
        return d;
    }

    /// <summary>
    /// À la charge du patient : 1) écriture inverse au relevé de la part encore due (rattachée à la vente) ; 2) trop-payé réparti au choix
    /// du patient : déduit de ses autres ventes choisies (mouvement « correction » imputé sur chacune, 087, restes relus ici) et/ou rendu par
    /// le tiroir (sortie « Remboursement d'un client » de la caisse du poste, bon de sortie). Toute dette changée entre-temps : refus.
    /// </summary>
    private static async Task<MouvementTiroirEcrit?> AnnulerRegularisationAsync(NpgsqlConnection cx, NpgsqlTransaction tx, RegularisationAAnnuler g,
        AnnulationAEcrire a, string numero, long? patientId, CancellationToken ct)
    {
        long? mouvement = null;
        if (patientId is { } patient)
        {
            if (g.DetteAnnulee > 0)
            {
                await DiminuerDetteAsync(cx, tx, patient, g.DetteAnnulee, a, $"Annulation de la vente {numero} : régularisation de part organisme annulée", ct);
                await using var lu = new NpgsqlCommand("SELECT currval(pg_get_serial_sequence('credit_mouvement', 'id'))", cx, tx);
                mouvement = (long)(await lu.ExecuteScalarAsync(ct))!;
            }
            if (g.Impute > 0)
            {
                foreach (var (vente, montant) in g.Imputations)
                {
                    await using var cmd = new NpgsqlCommand("SELECT reste FROM v_vente_credit WHERE vente_id = $1 AND patient_id = $2", cx, tx);
                    cmd.Parameters.AddWithValue(vente);
                    cmd.Parameters.AddWithValue(patient);
                    if (await cmd.ExecuteScalarAsync(ct) is not decimal reste || reste < montant)
                        throw new RegleMetierException("Une autre dette du patient vient de changer : rechargez la vente avant d’annuler.");
                }
                await DiminuerDetteAsync(cx, tx, patient, g.Impute, a,
                    $"Annulation de la vente {numero} : trop-payé de la régularisation déduit des autres dettes (choix du patient)", ct);
                foreach (var (vente, montant) in g.Imputations) await ReleveCredit.ImputerAsync(cx, tx, vente, montant, ct);
            }
        }
        else if (g.Impute > 0) throw new RegleMetierException("Vente sans patient : aucune autre dette à solder.");

        MouvementTiroirEcrit? bon = null;
        if (g.Rembourse > 0)
        {
            var session = a.SessionCaisseId ?? throw new RegleMetierException("Ouvrez la caisse du poste : le remboursement du patient sort du tiroir.");
            await TiroirSql.ExigerDansTiroirAsync(cx, tx, session, g.Rembourse, ct);
            string libelle;
            await using (var cmd = new NpgsqlCommand(
                "SELECT libelle FROM motif_reglable WHERE liste = 'tiroir' AND code = 'remboursement_client'", cx, tx))
                libelle = await cmd.ExecuteScalarAsync(ct) as string ?? "Remboursement d’un client";
            long evenement;
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO evenement_caisse (type, utilisateur_id, poste, session_caisse_id, montant, detail)
                VALUES ('tiroir_ouvert', $1, $2, $3, $4, $5) RETURNING id
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(a.UtilisateurId);
                cmd.Parameters.AddWithValue(a.Poste);
                cmd.Parameters.AddWithValue(session);
                cmd.Parameters.AddWithValue(g.Rembourse);
                cmd.Parameters.AddWithValue(libelle);
                evenement = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            bon = await TiroirSql.InsererAsync(cx, tx, evenement, session, a.UtilisateurId, a.Poste, "remboursement_client", libelle,
                Pharma.Domain.Caisse.RegleTiroir.Sortie, "patient", g.Rembourse, null, null, null, null, null, patientId,
                $"Trop-payé de la régularisation de part organisme, vente {numero} annulée", null, null, g.ValidePar, null, null, ct);
        }

        await using var maj = new NpgsqlCommand("""
            UPDATE regularisation_part_organisme
            SET statut = 'annulee', annulee_le = now(), annulee_par = $2, motif_annulation = $3, annulation_credit_mouvement_id = $4,
                annulation_dette_annulee = $5, annulation_trop_paye_impute = $6, annulation_rembourse = $7, annulation_mouvement_tiroir_id = $8
            WHERE id = $1 AND statut = 'active'
            """, cx, tx);
        maj.Parameters.AddWithValue(g.Id);
        maj.Parameters.AddWithValue(a.UtilisateurId);
        maj.Parameters.AddWithValue($"Annulation de la vente {numero} : {a.Motif}");
        maj.Parameters.Add(new NpgsqlParameter { Value = (object?)mouvement ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        maj.Parameters.AddWithValue(g.DetteAnnulee);
        maj.Parameters.AddWithValue(g.Impute);
        maj.Parameters.AddWithValue(g.Rembourse);
        maj.Parameters.Add(new NpgsqlParameter { Value = (object?)bon?.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        if (await maj.ExecuteNonQueryAsync(ct) == 0)
            throw new RegleMetierException("La régularisation de part organisme de cette vente vient d’être annulée : rechargez la vente.");
        return bon;
    }

    private static async Task DiminuerDetteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patient, decimal montant, AnnulationAEcrire a, string texte,
        CancellationToken ct)
    {
        await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, a.VenteId, a.UtilisateurId, a.Poste, texte, ct);
        await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx);
        cmd.Parameters.AddWithValue(patient);
        cmd.Parameters.AddWithValue(montant);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’annuler.");
    }
}
