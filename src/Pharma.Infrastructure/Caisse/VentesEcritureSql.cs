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
        // Clé d'encaissement : un même panier n'est enregistré qu'une fois (ux_vente_cle_encaissement). Un envoi simultané du même
        // panier attend ici la fin de l'autre transaction, puis est refusé par l'index : l'encaissement rend alors la vente enregistrée
        long venteId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO vente (numero, utilisateur_id, patient_id, ordonnance_id, type, organisme_id,
                               montant_total, part_organisme, part_patient, statut, session_caisse_id,
                               remise_pct, montant_remise, taux_tiers_payant, reste_saisi, remise_additionnee, montant_don,
                               cle_encaissement)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, 'en_cours', $10, $11, $12, $13, $14, $15, $16, $17)
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
            cmd.Parameters.Add(Valeur(a.CleEncaissement, NpgsqlDbType.Uuid));
            try { venteId = (long)(await cmd.ExecuteScalarAsync(ct))!; }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_vente_cle_encaissement"
                                              && a.CleEncaissement is { } cle)
            {
                throw new EncaissementDejaEnregistreException(cle);
            }
        }

        // Volet Chifa repris : n° de facture, date et QR brut ; un même volet n'est repris qu'une fois (ux_vente_chifa_facture)
        if (a.VoletChifa is { } volet)
        {
            // Date de l'ordonnance (7e zone du QR) gardée aussi sur la vente (script 095), comme au rattachement d'un volet
            await using var cmd = new NpgsqlCommand(
                "UPDATE vente SET chifa_facture = $2, chifa_date = $3, chifa_qr = $4, chifa_date_ordonnance = $5 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.AddWithValue(volet.NumeroFacture);
            cmd.Parameters.AddWithValue(volet.DateFacture);
            cmd.Parameters.AddWithValue(volet.Brut);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)volet.DateOrdonnance ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException($"Le volet Chifa n° {volet.NumeroFacture} vient d’être repris par une autre vente.", "volet_chifa");
            }
        }

        // « Remis à » / « Pour » (script 091) : fiche d'un patient ou nom libre ; la vente reste au nom de vente.patient_id
        if (a.RemisA is not null || a.Pour is not null)
        {
            await using var cmd = new NpgsqlCommand(
                "UPDATE vente SET remis_a_patient_id = $2, remis_a_nom = $3, pour_patient_id = $4, pour_nom = $5 WHERE id = $1", cx, tx);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.RemisA?.PatientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.RemisA?.Nom ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.Pour?.PatientId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.Pour?.Nom ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Canal de la part organisme (script 089) ; vente Chifa sans volet : n° de facture Chifa saisi (même unicité que le volet)
        if (a.CanalOrganisme != CanalOrganisme.Logiciel || a.FactureChifa is not null)
        {
            await using var cmd = new NpgsqlCommand(
                """
                UPDATE vente SET canal_organisme = $2, chifa_facture = COALESCE($3, chifa_facture),
                                 chifa_attente_motif_code = $4, chifa_attente_motif_texte = $5
                WHERE id = $1
                """, cx, tx);
            cmd.Parameters.AddWithValue(venteId);
            cmd.Parameters.AddWithValue(a.CanalOrganisme);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.FactureChifa ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            // Vente Chifa sans n° de facture : motif choisi (script 097)
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.MotifSansVoletCode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.MotifSansVoletTexte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
            {
                throw new RegleMetierException($"La facture Chifa n° {a.FactureChifa} vient d’être rattachée à une autre vente.", "facture_chifa");
            }
        }

        // 3. Lignes, stock, mouvements, registre
        // Lots du panier verrouillés d'emblée, toujours dans le même ordre (id croissant) : deux postes qui vendent les mêmes lots
        // s'attendent au lieu de s'interbloquer
        await using (var cmd = new NpgsqlCommand("SELECT id FROM lot WHERE id = ANY($1) ORDER BY id FOR UPDATE", cx, tx))
        {
            // Prestations (script 105) : sans lot, rien à verrouiller
            cmd.Parameters.AddWithValue(v.Lignes.Where(l => l.LotId is not null).Select(l => l.LotId!.Value).Distinct().Order().ToArray());
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var boitesRattachees = new HashSet<long>();
        foreach (var l in v.Lignes)
        {
            long ligneId;
            await using (var cmd = new NpgsqlCommand("""
                INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire,
                                         base_remboursement, taux_remboursement, montant_organisme, montant_patient, shp, raison_lot,
                                         remise_auto_pct, reduction_auto, reduction_manuelle, don_partiel, prix_chifa, choix_prix_chifa,
                                         type_prestation_id, libelle, taux_tva)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20) RETURNING id
                """, cx, tx))
            {
                cmd.Parameters.AddWithValue(venteId);
                cmd.Parameters.Add(Valeur(l.ProduitId, NpgsqlDbType.Bigint));
                cmd.Parameters.Add(Valeur(l.LotId, NpgsqlDbType.Bigint));
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
                cmd.Parameters.Add(Valeur(l.PrixChifa, NpgsqlDbType.Numeric));
                cmd.Parameters.Add(Valeur(l.ChoixPrixChifa, NpgsqlDbType.Text));
                cmd.Parameters.Add(Valeur(l.TypePrestationId, NpgsqlDbType.Bigint));
                cmd.Parameters.Add(Valeur(l.Libelle, NpgsqlDbType.Text));
                cmd.Parameters.Add(Valeur(l.TauxTva, NpgsqlDbType.Numeric));
                ligneId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }

            // Prestation (script 105) : ni boîte, ni stock, ni mouvement, ni registre
            if (l.LotId is not { } lotId || l.ProduitId is not { } produitId) continue;

            // Boîtes identifiées de ce lot : rattachées à la ligne (statut « vendue »), pour retrouver la vente au remboursement
            if (a.BoitesParLot?.GetValueOrDefault(lotId) is { Count: > 0 } boites)
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
                cmd.Parameters.AddWithValue(lotId);
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
                cmd.Parameters.AddWithValue(lotId);
                cmd.Parameters.AddWithValue(-l.Quantite);
                cmd.Parameters.AddWithValue(numero);
                cmd.Parameters.AddWithValue(v.UtilisateurId);
                await cmd.ExecuteNonQueryAsync(ct);
            }

            if (a.ProduitsSousRegistre.Contains(produitId))
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO registre_psychotrope (lot_id, ligne_vente_id, type_mouvement, quantite, solde_apres,
                                                      patient_id, medecin_id, ordonnance_id, utilisateur_id,
                                                      porteur_nom, porteur_adresse, porteur_piece)
                    VALUES ($1, $2, 'sortie', $3, $4, $5, $6, $7, $8, $9, $10, $11)
                    """, cx, tx);
                cmd.Parameters.AddWithValue(lotId);
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
            // Plafond revérifié dans la transaction : patient verrouillé, solde relu (deux ventes à crédit simultanées ne le
            // dépassent pas ensemble ; la seconde attend la première puis voit sa dette)
            await using (var verrou = new NpgsqlCommand("SELECT solde_credit, plafond_credit FROM patient WHERE id = $1 FOR UPDATE", cx, tx))
            {
                verrou.Parameters.AddWithValue(v.PatientId!.Value);
                await using var r = await verrou.ExecuteReaderAsync(ct);
                if (!await r.ReadAsync(ct)) throw new RegleMetierException("Crédit : patient introuvable.");
                var (solde, plafond) = (r.GetDecimal(0), r.GetDecimal(1));
                if (!PlafondEncaissement.Respecte(solde, a.Reglement.Montant, plafond, a.PeutDepasserPlafond))
                    throw new Pharma.Application.Commun.DroitRefuseException(Permissions.CreditDepasserPlafond, "dépasser le plafond de crédit",
                        PlafondEncaissement.Message(solde, a.Reglement.Montant, plafond));
            }
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
        if (a.ProduitsSousRegistre.Count > 0 && v.Lignes.Any(l => a.ProduitsSousRegistre.Contains(l.ProduitId ?? 0)))
            await JournalSql.EcrireAsync(cx, tx, v.UtilisateurId, "delivrance_psychotrope", "vente", venteId,
                new Dictionary<string, object?> { ["ticket"] = numero, ["ordonnance_id"] = ordonnanceId },
                Permissions.PsychotropesDelivrer, ct, autorisation?.AutoriseParId);

        await tx.CommitAsync(ct);
        return (venteId, numero);
    }

    public async Task<VenteDejaEncaissee?> VenteParCleAsync(Guid cle, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT v.id, v.numero, v.montant_total, v.part_organisme, v.part_patient,
                   (SELECT p.mode FROM paiement p WHERE p.vente_id = v.id ORDER BY p.id LIMIT 1),
                   v.statut = 'annulee'
            FROM vente v
            WHERE v.cle_encaissement = $1 AND v.statut IN ('validee', 'annulee')
            """);
        cmd.Parameters.AddWithValue(cle);
        VenteDejaEncaissee vente;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            if (!await r.ReadAsync(ct)) return null;
            vente = new VenteDejaEncaissee(r.GetInt64(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4),
                r.IsDBNull(5) ? null : CodesMetier.ModePaiementDepuisCode(r.GetString(5)), r.GetBoolean(6));
        }
        // Lignes enregistrées : comparées au panier renvoyé (contenu différent : refus)
        await using var lignes = db.CreateCommand("SELECT produit_id, lot_id, quantite, type_prestation_id FROM ligne_vente WHERE vente_id = $1 ORDER BY id");
        lignes.Parameters.AddWithValue(vente.VenteId);
        var liste = new List<LigneDejaEncaissee>();
        await using (var r = await lignes.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                liste.Add(new LigneDejaEncaissee(r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3)));
        return vente with { Lignes = liste };
    }

    private static async Task<object?> Scalaire(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        return await cmd.ExecuteScalarAsync(ct);
    }

    private static NpgsqlParameter Valeur(object? valeur, NpgsqlDbType type) =>
        new() { Value = valeur ?? DBNull.Value, NpgsqlDbType = type };
}
