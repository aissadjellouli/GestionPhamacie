using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Retour de médicaments par le patient (028_retour_client.sql), en une transaction.</summary>
public sealed class RetoursClientSql(NpgsqlDataSource db) : IRetoursClient
{
    public async Task<VenteRetournable?> LireAsync(long venteId, CancellationToken ct = default)
    {
        if (await new AnnulationsVenteSql(db).TrouverAsync(venteId, null, ct) is not { } vente) return null;

        var lignes = new List<LigneRetournable>();
        await using (var cmd = db.CreateCommand("""
            SELECT l.id, l.lot_id, p.nom_commercial, lo.numero_lot, l.quantite,
                   COALESCE((SELECT SUM(r.quantite) FROM retour_client_ligne r WHERE r.ligne_vente_id = l.id), 0)::int,
                   -- Part du patient de la ligne, réductions de la ligne déduites (remise automatique, remise du vendeur, don partiel) :
                   -- le retour rend ce que le patient a payé pour ce produit-là (ventes antérieures : prorata de la remise de la vente)
                   l.montant_patient - l.reduction_auto - l.reduction_manuelle,
                   -- Part de l'organisme de la ligne enregistrée à la caisse (script 155 : retirée au prorata des boîtes rendues)
                   l.montant_organisme
            -- Lignes produit seulement : une prestation (script 105, D-12) ne se rend pas (annulation de la vente ou avoir)
            FROM ligne_vente l JOIN produit p ON p.id = l.produit_id JOIN lot lo ON lo.id = l.lot_id
            WHERE l.vente_id = $1 ORDER BY l.id
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneRetournable(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetDecimal(6), [],
                    r.GetDecimal(7)));
        }

        // Part de l'organisme (script 155) : part retenue hors majoration (Chifa si régularisée, plus la part rendue avant le rattachement,
        // script 157 ; sinon caisse), déjà retirée par des
        // retours, canal, facture d'organisme émise par le logiciel (avoir AV- manuel), n° de facture Chifa
        OrganismeRetour? organisme = null;
        await using (var cmd = db.CreateCommand("""
            SELECT v.canal_organisme, COALESCE(g.part_organisme_chifa + g.part_organisme_rendue_avant, v.part_organisme),
                   COALESCE((SELECT sum(rl.montant_organisme) FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
                             WHERE l.vente_id = v.id), 0),
                   (SELECT fc.numero FROM facture_client_ligne fl JOIN facture_client fc ON fc.id = fl.facture_id
                    WHERE fl.vente_id = v.id AND fl.part = 'organisme' AND fc.type = 'facture' AND fc.statut IN ('emise', 'rejetee')
                    ORDER BY fc.id LIMIT 1),
                   v.chifa_facture
            FROM vente v LEFT JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active'
            WHERE v.id = $1
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                organisme = new OrganismeRetour(r.GetDecimal(1), r.GetDecimal(2), r.GetString(0), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4));
        }

        // Boîtes identifiées encore chez le patient
        var boites = new Dictionary<long, List<BoiteVendue>>();
        await using (var cmd = db.CreateCommand("""
            -- Boîte identifiée par son n° de série (Data Matrix) ou par l'étiquette de la pharmacie (pas de n° de série)
            SELECT b.ligne_vente_id, b.id, COALESCE(b.numero_serie, b.code_etiquette, 'boîte ' || b.id) AS repere
            FROM boite b JOIN ligne_vente l ON l.id = b.ligne_vente_id
            WHERE l.vente_id = $1 AND b.statut = 'vendue' ORDER BY repere
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!boites.TryGetValue(r.GetInt64(0), out var liste)) boites[r.GetInt64(0)] = liste = [];
                liste.Add(new BoiteVendue(r.GetInt64(1), r.GetString(2)));
            }
        }
        return new VenteRetournable(vente, lignes.Select(l => l with { Boites = boites.GetValueOrDefault(l.LigneVenteId) ?? [] }).ToList(), organisme);
    }

    public async Task EnregistrerAsync(RetourAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Vente verrouillée : deux retours simultanés ne peuvent pas rendre plus que vendu. Le rattachement à un bordereau
        // (BordereauxSql.AjouterVentesAsync) met à jour la même ligne de vente : il attend ce verrou, ou ce retour attend le sien ;
        // l'état du bordereau est donc relu ici, sous le verrou (script 155)
        bool dansUnBordereau, deposee;
        await using (var cmd = new NpgsqlCommand("""
            SELECT v.statut, v.patient_id, v.bordereau_id IS NOT NULL,
                   EXISTS (SELECT 1 FROM bordereau_vente bv JOIN bordereau b ON b.id = bv.bordereau_id
                           WHERE bv.vente_id = v.id AND b.statut <> 'preparation')
            FROM vente v WHERE v.id = $1 FOR UPDATE OF v
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetString(0) != "validee") throw new RegleMetierException("Cette vente n’est plus encaissée (annulée ?).");
            (dansUnBordereau, deposee) = (r.GetBoolean(2), r.GetBoolean(3));
        }
        var partOrganisme = a.Lignes.Sum(l => l.MontantOrganisme);
        if (partOrganisme > 0)
        {
            // Même règle que l'application, revérifiée sous le verrou : vente sur aucun bordereau
            RegleRetour.VerifierPartOrganisme(deposee, dansUnBordereau);
            // Part organisme retirée : jamais plus que ce qui reste de la part retenue (hors majoration)
            await using var cmd = new NpgsqlCommand("""
                SELECT COALESCE(g.part_organisme_chifa + g.part_organisme_rendue_avant, v.part_organisme)
                     - COALESCE((SELECT sum(rl.montant_organisme) FROM retour_client_ligne rl JOIN ligne_vente l ON l.id = rl.ligne_vente_id
                                 WHERE l.vente_id = v.id), 0)
                FROM vente v LEFT JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active'
                WHERE v.id = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            if ((decimal)(await cmd.ExecuteScalarAsync(ct))! < partOrganisme)
                throw new RegleMetierException("La part de l’organisme de cette vente vient de changer (autre retour ou régularisation) : rechargez la vente.");
        }
        // Part de la régularisation « à la charge du patient » rendue (script 158) : jamais plus que ce qui en reste (régularisation active)
        var partRegularisation = a.Lignes.Sum(l => l.MontantRegularisation);
        if (partRegularisation > 0)
        {
            await using var cmd = new NpgsqlCommand($"""
                SELECT g.montant_patient - {AnnulationsVenteSql.RegularisationRendue}
                FROM vente v JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active' AND g.sort = 'charge_patient'
                WHERE v.id = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal reste || reste < partRegularisation)
                throw new RegleMetierException("La régularisation de part organisme de cette vente vient de changer : rechargez la vente.");
        }
        // Part de la régularisation « rendu au patient » retenue (script 159) : jamais plus que ce qui en reste (régularisation active)
        var avoirRetenu = a.Lignes.Sum(l => l.MontantAvoirRetenu);
        if (avoirRetenu > 0 || a.DetteAvoirRetenu > 0)
        {
            await using var cmd = new NpgsqlCommand($"""
                SELECT g.montant_patient - {AnnulationsVenteSql.AvoirRetenu}
                FROM vente v JOIN regularisation_part_organisme g ON g.vente_id = v.id AND g.statut = 'active' AND g.sort = 'avoir_patient'
                WHERE v.id = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            if (await cmd.ExecuteScalarAsync(ct) is not decimal reste || reste < avoirRetenu || a.DetteAvoirRetenu > avoirRetenu)
                throw new RegleMetierException("La régularisation de part organisme de cette vente vient de changer : rechargez la vente.");
        }
        // Sous le verrou de la vente : l'avoir recrédité ne dépasse jamais ce qui a été payé par avoir moins ce que les retours ont déjà recrédité
        if (a.AvoirRecredite > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT (SELECT COALESCE(SUM(montant), 0) FROM paiement WHERE vente_id = $1 AND mode = 'avoir')
                     - (SELECT COALESCE(SUM(montant_rembourse), 0) FROM retour_client WHERE vente_id = $1 AND mode = 'avoir')
                """, cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            if ((decimal)(await cmd.ExecuteScalarAsync(ct))! < a.AvoirRecredite)
                throw new RegleMetierException("Un autre retour vient d’être enregistré sur cette vente : rechargez la vente.");
        }
        foreach (var l in a.Lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT l.quantite - COALESCE((SELECT SUM(quantite) FROM retour_client_ligne WHERE ligne_vente_id = l.id), 0)
                FROM ligne_vente l WHERE l.id = $1 AND l.vente_id = $2
                  AND l.lot_id IS NOT NULL   -- prestation (script 105, D-12) : jamais rendue
                """, cx, tx);
            cmd.Parameters.AddWithValue(l.LigneVenteId);
            cmd.Parameters.AddWithValue(a.VenteId);
            if (await cmd.ExecuteScalarAsync(ct) is not long reste || reste < l.Quantite)
                throw new RegleMetierException("Une partie de ces boîtes vient déjà d’être rendue : rechargez la vente.");
        }

        // Dette diminuée (vente à crédit, ou dette soldée au choix du caissier) : une écriture « credit » pour cette part,
        // et une seconde écriture (sans ligne) pour l'argent rendu. Le total des deux reste le « déjà rendu » de la vente ;
        // seule l'écriture de l'argent rendu sort de la caisse ou de la banque.
        // Part recréditée en avoir (script 109, vente payée en partie par avoir) : une écriture « avoir », qui ne sort ni de la caisse
        // ni de la banque, mais compte dans le « déjà rendu » de la vente.
        // Script 158 : la part impayée de la régularisation et l'argent imputé sur les dettes du patient sont aussi des écritures « credit »
        // (rien ne sort du tiroir) ; l'argent rendu sans mode de paiement (part du patient nulle) sort en espèces.
        var rendu = a.Rendu;
        var modeRendu = a.Mode == "credit" || (a.Mode is null && rendu > 0) ? "especes" : a.Mode;
        var parts = new List<(string? Mode, decimal Montant)>();
        if (a.AvoirRecredite > 0) parts.Add((RegleAvoir.Mode, a.AvoirRecredite));
        var dette = a.ReductionDette + a.ReductionRegularisation + a.ImputationDettes;
        if (dette > 0) parts.Add(("credit", dette));
        if (rendu > 0 || parts.Count == 0) parts.Add((a.Mode == "credit" && rendu == 0 ? "credit" : modeRendu, rendu));
        // Les lignes rendues sont rattachées à la première écriture
        // Dette ajoutée par la retenue (script 159) : portée par la première écriture, celle qui porte les lignes
        var retourId = await InsererRetourAsync(cx, tx, a, parts[0].Mode, parts[0].Montant, ct, a.DetteAvoirRetenu);
        foreach (var (mode, montant) in parts.Skip(1))
            await InsererRetourAsync(cx, tx, a, mode, montant, ct);
        if (a.AvoirRecredite > 0)
            await EcritureAvoir.CrediterAsync(cx, tx, a.PatientAvoirId ?? throw new RegleMetierException("Vente sans patient : aucun avoir possible."),
                a.AvoirRecredite, RegleAvoir.OrigineRetourClient, a.VenteId, $"Retour client {a.Numero} : {a.Motif}", a.UtilisateurId, ct);

        var reference = $"Retour client {a.Numero}";
        foreach (var l in a.Lignes)
        {
            await Executer(cx, tx, """
                INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant, montant_organisme, montant_regularisation,
                                                 montant_avoir_retenu)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """, ct, retourId, l.LigneVenteId, l.Quantite, l.Destination.Code(), l.Montant, l.MontantOrganisme, l.MontantRegularisation,
                l.MontantAvoirRetenu);

            // La boîte revient à l'officine (lot et registre), puis repart à la destruction si le pharmacien l'a décidé
            await Executer(cx, tx, "UPDATE lot SET quantite_disponible = quantite_disponible + $2 WHERE id = $1", ct, l.LotId, l.Quantite);
            await Executer(cx, tx, "INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id) VALUES ($1, 'retour_client', $2, $3, $4)",
                ct, l.LotId, l.Quantite, reference, a.UtilisateurId);
            if (l.Destination == DestinationRetour.Destruction)
            {
                await Executer(cx, tx, "UPDATE lot SET quantite_disponible = quantite_disponible - $2 WHERE id = $1", ct, l.LotId, l.Quantite);
                await Executer(cx, tx, "INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id) VALUES ($1, 'destruction', $2, $3, $4)",
                    ct, l.LotId, -l.Quantite, $"{reference} : à détruire", a.UtilisateurId);
            }

            if (l.Boites.Count > 0)
            {
                // Sous le verrou de la vente : chaque boîte est encore vendue sur cette ligne (jamais rendue deux fois)
                await using (var cmd = new NpgsqlCommand("SELECT count(*) FROM boite WHERE id = ANY($1) AND statut = 'vendue' AND ligne_vente_id = $2", cx, tx))
                {
                    cmd.Parameters.AddWithValue(l.Boites.ToArray());
                    cmd.Parameters.AddWithValue(l.LigneVenteId);
                    if ((long)(await cmd.ExecuteScalarAsync(ct))! != l.Boites.Distinct().Count())
                        throw new RegleMetierException("Une de ces boîtes vient déjà d’être rendue ou n’est plus rattachée à la vente : rechargez la vente.");
                }
                await Executer(cx, tx, """
                    INSERT INTO boite_evenement (boite_id, type, ligne_vente_id, utilisateur_id, commentaire)
                    SELECT id, 'retour_client', ligne_vente_id, $2, $3 FROM boite WHERE id = ANY($1)
                    """, ct, l.Boites.ToArray(), a.UtilisateurId, $"{reference} : {a.Motif}");
                if (l.Destination == DestinationRetour.Destruction)
                {
                    await Executer(cx, tx, """
                        INSERT INTO boite_evenement (boite_id, type, utilisateur_id, commentaire)
                        SELECT id, 'destruction', $2, $3 FROM boite WHERE id = ANY($1)
                        """, ct, l.Boites.ToArray(), a.UtilisateurId, "Reprise au patient, à détruire");
                    await Executer(cx, tx, "UPDATE boite SET statut = 'detruite', ligne_vente_id = NULL WHERE id = ANY($1)", ct, l.Boites.ToArray());
                }
                else
                    await Executer(cx, tx, "UPDATE boite SET statut = 'en_stock', ligne_vente_id = NULL WHERE id = ANY($1)", ct, l.Boites.ToArray());
            }
        }

        // Vente à crédit : la dette diminue de ce qui reste dû (calculé avant) ; si elle a bougé entre-temps, on refuse
        if (a.ReductionDette > 0)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Retour, a.VenteId, a.UtilisateurId, a.Poste, $"Retour client {a.Numero} : {a.Motif}{(a.AccordClient ? " (déduit de la dette avec l’accord du client)" : "")}", ct);
            await using var cmd = new NpgsqlCommand("""
                UPDATE patient SET solde_credit = solde_credit - $2
                WHERE id = COALESCE($3, (SELECT patient_id FROM vente WHERE id = $1)) AND solde_credit >= $2
                """, cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(a.ReductionDette);
            // Vente sans patient : dette du patient qui s'est nommé au retour
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.PatientDetteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’enregistrer le retour.");
        }

        // Script 158 : part impayée de la régularisation des boîtes rendues retirée de la dette (mouvement « correction » rattaché à la
        // vente, comme la régularisation elle-même) ; puis argent dû au patient imputé sur ses dettes, écrit comme « Encaisser une dette »
        // (mouvement « remboursement », ventes à crédit les plus anciennes d'abord, au plus leur reste), sans paiement ni tiroir
        if (a.ReductionRegularisation > 0)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, a.VenteId, a.UtilisateurId, a.Poste,
                $"Retour client {a.Numero} : part de la régularisation Chifa des boîtes rendues", ct);
            await DiminuerDetteVenteAsync(cx, tx, a.VenteId, a.ReductionRegularisation, ct);
        }
        if (a.ImputationDettes > 0) await ImputerSurDettesAsync(cx, tx, a, ct);
        // Script 159 : part de la retenue (régularisation « rendu au patient ») que le retour ne couvrait pas, ajoutée à la dette du patient
        // (mouvement « correction » rattaché à la vente, comme la régularisation ; compté par v_credit_accorde)
        if (a.DetteAvoirRetenu > 0)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, a.VenteId, a.UtilisateurId, a.Poste,
                $"Retour client {a.Numero} : part de la régularisation Chifa « rendu au patient » des boîtes rendues, non couverte par le remboursement", ct);
            await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit + $2 WHERE id = (SELECT patient_id FROM vente WHERE id = $1)", cx, tx);
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(a.DetteAvoirRetenu);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("Vente sans patient : la part non couverte ne peut pas devenir une dette.");
        }

        await tx.CommitAsync(ct);
    }

    private static async Task DiminuerDetteVenteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long venteId, decimal montant, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            UPDATE patient SET solde_credit = solde_credit - $2
            WHERE id = (SELECT patient_id FROM vente WHERE id = $1) AND solde_credit >= $2
            """, cx, tx);
        cmd.Parameters.AddWithValue(venteId);
        cmd.Parameters.AddWithValue(montant);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’enregistrer le retour.");
    }

    /// <summary>
    /// Argent dû au patient imputé sur ses dettes (script 158), sous verrou du patient : restes relus après les écritures du retour
    /// (v_vente_credit), répartis comme le choix par défaut d'« Encaisser une dette » (plus anciennes d'abord, au plus leur reste ; le
    /// surplus solde la dette la plus ancienne), mouvement « remboursement » au relevé et parts gardées dans credit_imputation (087).
    /// </summary>
    private static async Task ImputerSurDettesAsync(NpgsqlConnection cx, NpgsqlTransaction tx, RetourAEcrire a, CancellationToken ct)
    {
        long patient;
        await using (var cmd = new NpgsqlCommand("""
            SELECT p.id FROM patient p WHERE p.id = (SELECT patient_id FROM vente WHERE id = $1) FOR UPDATE
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            patient = await cmd.ExecuteScalarAsync(ct) as long? ?? throw new RegleMetierException("Vente sans patient : aucune dette à solder.");
        }
        var restes = new List<(long, decimal)>();
        await using (var cmd = new NpgsqlCommand("""
            SELECT vente_id, reste FROM v_vente_credit WHERE patient_id = $1 AND reste > 0 ORDER BY date_vente, vente_id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(patient);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) restes.Add((r.GetInt64(0), r.GetDecimal(1)));
        }
        var parts = RegleReglementCredit.Imputer(a.ImputationDettes, restes);
        await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Remboursement, null, a.UtilisateurId, a.Poste,
            $"Encaissement de dette (retour client {a.Numero}, sans argent : déduit du remboursement avec l’accord du client)", ct, a.SessionCaisseId);
        await using (var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx))
        {
            cmd.Parameters.AddWithValue(patient);
            cmd.Parameters.AddWithValue(a.ImputationDettes);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’enregistrer le retour.");
        }
        foreach (var (vente, montant) in parts) await ReleveCredit.ImputerAsync(cx, tx, vente, montant, ct);
    }

    private static async Task<long> InsererRetourAsync(NpgsqlConnection cx, NpgsqlTransaction tx, RetourAEcrire a, string? mode, decimal montant,
        CancellationToken ct, decimal detteAvoirRetenu = 0)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse, session_caisse_id, poste, motif_code, motif_texte,
                                       dette_avoir_retenu)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(a.VenteId);
        cmd.Parameters.AddWithValue(a.Motif);
        cmd.Parameters.AddWithValue(a.UtilisateurId);
        cmd.Parameters.AddWithValue(a.ValidePar);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)mode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(a.Poste);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.MotifCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.MotifTexte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(detteAvoirRetenu);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static async Task Executer(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
