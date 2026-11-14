using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Infrastructure.Securite;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Enregistre une vente en une transaction. Les déclencheurs restent le filet de sécurité :
/// lot périmé ou bloqué (verifier_lot_vendable), patient obligatoire (verifier_tracabilite_vente).
/// </summary>
public sealed class VentesEcritureSql(NpgsqlDataSource db, Pharma.Application.Securite.AutorisationCourante? autorisation = null) : IVentesEcriture
{
    public async Task<(long Id, string Numero)> EnregistrerAsync(VenteAEnregistrer a, CancellationToken ct = default)
    {
        var v = a.Vente;
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // La caisse doit être encore ouverte, et le rester jusqu'à la fin de la transaction : le verrou partagé fait
        // attendre une clôture simultanée (SessionsCaisseSql.CloturerAsync), qui relit ensuite les espèces attendues
        if (v.SessionCaisseId is { } sessionId)
        {
            await using var cmd = new NpgsqlCommand("SELECT 1 FROM session_caisse WHERE id = $1 AND fermee_le IS NULL FOR SHARE", cx, tx);
            cmd.Parameters.AddWithValue(sessionId);
            if (await cmd.ExecuteScalarAsync(ct) is null)
                throw new RegleMetierException("La caisse de ce poste vient d’être clôturée : rouvrez-la avant d’encaisser.");
        }

        var numero = (string)(await Scalaire(cx, tx, "SELECT prochain_numero_vente()", ct))!;

        // 1. Ordonnance (obligatoire pour un psychotrope)
        long? ordonnanceId = null, medecinId = null;
        if (a.Ordonnance is { } o)
        {
            medecinId = o.MedecinId;
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO ordonnance (patient_id, medecin_id, date_prescription, numero, chemin_scan, texte_reconnu)
                VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
                """, cx, tx);
            cmd.Parameters.Add(Valeur(v.PatientId, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(Valeur(o.MedecinId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(o.DatePrescription);
            cmd.Parameters.Add(Valeur(o.Numero, NpgsqlDbType.Text));
            cmd.Parameters.Add(Valeur(o.JetonScan, NpgsqlDbType.Text));
            cmd.Parameters.Add(Valeur(o.TexteReconnu, NpgsqlDbType.Text));
            ordonnanceId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // 2. Vente (en cours jusqu'à la fin : le contrôle de traçabilité s'applique au passage à « validee »)
        long venteId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO vente (numero, utilisateur_id, patient_id, ordonnance_id, type, organisme_id,
                               montant_total, part_organisme, part_patient, statut, session_caisse_id,
                               remise_pct, montant_remise, taux_tiers_payant, reste_saisi, remise_additionnee, montant_don)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'en_cours', $10, $11, $12, $13, $14, $15, $16)
            RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(numero);
            cmd.Parameters.AddWithValue(v.UtilisateurId);
            cmd.Parameters.Add(Valeur(v.PatientId, NpgsqlDbType.Bigint));
            cmd.Parameters.Add(Valeur(ordonnanceId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(v.Type.Code());
            cmd.Parameters.Add(Valeur(v.OrganismeId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(v.MontantTotal);
            cmd.Parameters.AddWithValue(v.PartOrganisme);
            cmd.Parameters.AddWithValue(v.PartPatient);
            cmd.Parameters.Add(Valeur(v.SessionCaisseId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(v.RemisePct);
            cmd.Parameters.AddWithValue(v.MontantRemise);
            cmd.Parameters.Add(Valeur(v.TauxTiersPayant, NpgsqlDbType.Numeric));
            cmd.Parameters.AddWithValue(v.ResteSaisi is not null);
            cmd.Parameters.AddWithValue(v.RemiseAdditionnee);
            cmd.Parameters.AddWithValue(v.MontantDon);
            venteId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Volet Chifa repris : n° de facture, date et QR brut ; un même volet n'est repris qu'une fois (ux_vente_chifa_facture)
        if (a.VoletChifa is { } volet)
        {
            await using var cmd = new NpgsqlCommand("UPDATE vente SET chifa_facture = $2, chifa_date = $3, chifa_qr = $4 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.AddWithValue(volet.NumeroFacture);
            cmd.Parameters.AddWithValue(volet.DateFacture);
            cmd.Parameters.AddWithValue(volet.Brut);
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException($"Le volet Chifa n° {volet.NumeroFacture} vient d’être repris par une autre vente.", "volet_chifa");
            }
        }

        // 3. Lignes, stock, mouvements, registre
        var boitesRattachees = new HashSet<long>();
        foreach (var l in v.Lignes)
        {
            long ligneId;
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire,
                                         base_remboursement, taux_remboursement, montant_organisme, montant_patient, shp, raison_lot,
                                         remise_auto_pct, reduction_auto, reduction_manuelle, don_partiel)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) RETURNING id
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(venteId);
                cmd.Parameters.AddWithValue(l.ProduitId);
                cmd.Parameters.AddWithValue(l.LotId);
                cmd.Parameters.AddWithValue(l.Quantite);
                cmd.Parameters.AddWithValue(l.PrixUnitaire);
                cmd.Parameters.AddWithValue(l.BaseRemboursement);
                cmd.Parameters.AddWithValue(l.TauxRemboursement);
                cmd.Parameters.AddWithValue(l.MontantOrganisme);
                cmd.Parameters.AddWithValue(l.MontantPatient);
                cmd.Parameters.AddWithValue(l.Shp);
                cmd.Parameters.Add(Valeur(l.RaisonLot, NpgsqlDbType.Text));
                cmd.Parameters.AddWithValue(l.RemiseAutoPct);
                cmd.Parameters.AddWithValue(l.ReductionAuto);
                cmd.Parameters.AddWithValue(l.ReductionManuelle);
                cmd.Parameters.AddWithValue(l.Medicament && l.Reduction > 0);
                ligneId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            // Boîtes identifiées de ce lot : rattachées à la ligne (statut « vendue »), pour retrouver la vente au remboursement
            if (a.BoitesParLot?.GetValueOrDefault(l.LotId) is { Count: > 0 } boites)
                foreach (var boite in boites.Except(boitesRattachees).Take(l.Quantite).ToList())
                {
                    await using var cmd = new NpgsqlCommand("SELECT vendre_boite($1, $2, $3)", cx, tx);
                    cmd.Parameters.AddWithValue(boite);
                    cmd.Parameters.AddWithValue(ligneId);
                    cmd.Parameters.AddWithValue(v.UtilisateurId);
                    try { await cmd.ExecuteNonQueryAsync(ct); }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.RaiseException)
                    {
                        throw new RegleMetierException($"Boîte scannée : {e.MessageText}");
                    }
                    boitesRattachees.Add(boite);
                }

            int resteLot;
            await using (var cmd = new NpgsqlCommand("""
                UPDATE lot SET quantite_disponible = quantite_disponible - $2
                WHERE id = $1 AND quantite_disponible >= $2
                RETURNING quantite_disponible, numero_lot
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(l.LotId);
                cmd.Parameters.AddWithValue(l.Quantite);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct))
                    throw new RegleMetierException("Stock insuffisant : une boîte de ce lot vient d’être vendue sur un autre poste. Rescannez le produit.");
                resteLot = r.GetInt32(0);
            }

            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
                VALUES ($1, 'vente', $2, $3, $4)
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(l.LotId);
                cmd.Parameters.AddWithValue(-l.Quantite);
                cmd.Parameters.AddWithValue(numero);
                cmd.Parameters.AddWithValue(v.UtilisateurId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (a.ProduitsSousRegistre.Contains(l.ProduitId))
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO registre_psychotrope (lot_id, ligne_vente_id, type_mouvement, quantite, solde_apres,
                                                      patient_id, medecin_id, ordonnance_id, utilisateur_id,
                                                      porteur_nom, porteur_adresse, porteur_piece)
                    VALUES ($1, $2, 'sortie', $3, $4, $5, $6, $7, $8, $9, $10, $11)
                    """, cx, tx);
                cmd.Parameters.AddWithValue(l.LotId);
                cmd.Parameters.AddWithValue(ligneId);
                cmd.Parameters.AddWithValue(l.Quantite);
                cmd.Parameters.AddWithValue(resteLot);
                cmd.Parameters.Add(Valeur(v.PatientId, NpgsqlDbType.Bigint));
                cmd.Parameters.Add(Valeur(medecinId, NpgsqlDbType.Bigint));
                cmd.Parameters.Add(Valeur(ordonnanceId, NpgsqlDbType.Bigint));
                cmd.Parameters.AddWithValue(v.UtilisateurId);
                var porteur = a.Ordonnance?.Porteur;
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)porteur?.Nom ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)porteur?.Adresse ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)porteur?.PieceIdentite ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }

        // 4. Paiement de la part du patient (rien à encaisser si l'organisme paie tout)
        if (a.Reglement.Montant > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO paiement (vente_id, patient_id, mode, montant, utilisateur_id, session_caisse_id)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, cx, tx);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.Add(Valeur(v.PatientId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(a.Reglement.Mode.Code());
            cmd.Parameters.AddWithValue(a.Reglement.Montant);
            cmd.Parameters.AddWithValue(v.UtilisateurId);
            cmd.Parameters.Add(Valeur(v.SessionCaisseId, NpgsqlDbType.Bigint));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 5. Crédit : la dette du patient augmente
        if (a.CreditAccorde)
        {
            await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.VenteCredit, venteId, v.UtilisateurId, null, $"Vente {numero}", ct, v.SessionCaisseId);
            await using var cmd = new NpgsqlCommand(
                "UPDATE patient SET solde_credit = solde_credit + $2 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(v.PatientId!.Value);
            cmd.Parameters.AddWithValue(a.Reglement.Montant);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 6. Validation (déclenche le contrôle de traçabilité en fin de transaction)
        await using (var cmd = new NpgsqlCommand("UPDATE vente SET statut = 'validee' WHERE id = $1", cx, tx))
        {
            cmd.Parameters.AddWithValue(venteId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // 7. Journal des actions sensibles
        // Remise du vendeur (hors médicaments), don partiel sur des médicaments, remises automatiques : chacun inscrit au journal
        var remiseVendeur = v.Lignes.Where(l => !l.Medicament).Sum(l => l.ReductionManuelle);
        var donVendeur = v.Lignes.Where(l => l.Medicament).Sum(l => l.ReductionManuelle);
        if (remiseVendeur > 0)
            await JournalSql.EcrireAsync(cx, tx, v.UtilisateurId, "remise", "vente", venteId,
                new Dictionary<string, object?>
                {
                    ["ticket"] = numero, ["pourcentage"] = v.RemisePct, ["montant"] = remiseVendeur, ["additionnee"] = v.RemiseAdditionnee
                },
                Permissions.CaisseRemise, ct, autorisation?.AutoriseParId);
        if (donVendeur > 0)
            await JournalSql.EcrireAsync(cx, tx, v.UtilisateurId, "don_partiel", "vente", venteId,
                new Dictionary<string, object?> { ["ticket"] = numero, ["pourcentage"] = v.RemisePct, ["montant"] = donVendeur },
                Permissions.StockDonner, ct, autorisation?.AutoriseParId);
        if (v.MontantReductionsAuto > 0)
            await JournalSql.EcrireAsync(cx, tx, v.UtilisateurId, "remise_automatique_appliquee", "vente", venteId,
                new Dictionary<string, object?>
                {
                    ["ticket"] = numero, ["montant"] = v.MontantReductionsAuto,
                    ["dont_don_partiel"] = v.Lignes.Where(l => l.Medicament).Sum(l => l.ReductionAuto)
                }, null, ct);
        if (a.ProduitsSousRegistre.Count > 0 && v.Lignes.Any(l => a.ProduitsSousRegistre.Contains(l.ProduitId)))
            await JournalSql.EcrireAsync(cx, tx, v.UtilisateurId, "delivrance_psychotrope", "vente", venteId,
                new Dictionary<string, object?> { ["ticket"] = numero, ["ordonnance_id"] = ordonnanceId },
                Permissions.PsychotropesDelivrer, ct, autorisation?.AutoriseParId);

        await tx.CommitAsync(ct);
        return (venteId, numero);
    }

    private static async Task<object?> Scalaire(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static NpgsqlParameter Valeur(object? valeur, NpgsqlDbType type) =>
        new() { Value = valeur ?? DBNull.Value, NpgsqlDbType = type };
}
