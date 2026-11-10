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
                   l.montant_patient
            FROM ligne_vente l JOIN produit p ON p.id = l.produit_id JOIN lot lo ON lo.id = l.lot_id
            WHERE l.vente_id = $1 ORDER BY l.id
            """))
        {
            cmd.Parameters.AddWithValue(venteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                lignes.Add(new LigneRetournable(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5), r.GetDecimal(6), []));
        }

        // Boîtes identifiées encore chez le patient
        var boites = new Dictionary<long, List<BoiteVendue>>();
        await using (var cmd = db.CreateCommand("""
            SELECT b.ligne_vente_id, b.id, b.numero_serie FROM boite b JOIN ligne_vente l ON l.id = b.ligne_vente_id
            WHERE l.vente_id = $1 AND b.statut = 'vendue' ORDER BY b.numero_serie
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
        return new VenteRetournable(vente, lignes.Select(l => l with { Boites = boites.GetValueOrDefault(l.LigneVenteId) ?? [] }).ToList());
    }

    public async Task EnregistrerAsync(RetourAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);

        // Vente verrouillée : deux retours simultanés ne peuvent pas rendre plus que vendu
        await using (var cmd = new NpgsqlCommand("SELECT statut, patient_id FROM vente WHERE id = $1 FOR UPDATE", cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct) || r.GetString(0) != "validee") throw new RegleMetierException("Cette vente n’est plus encaissée (annulée ?).");
        }
        foreach (var l in a.Lignes)
        {
            await using var cmd = new NpgsqlCommand("""
                SELECT l.quantite - COALESCE((SELECT SUM(quantite) FROM retour_client_ligne WHERE ligne_vente_id = l.id), 0)
                FROM ligne_vente l WHERE l.id = $1 AND l.vente_id = $2
                """, cx, tx);
            cmd.Parameters.AddWithValue(l.LigneVenteId);
            cmd.Parameters.AddWithValue(a.VenteId);
            if (await cmd.ExecuteScalarAsync(ct) is not long reste || reste < l.Quantite)
                throw new RegleMetierException("Une partie de ces boîtes vient déjà d’être rendue : rechargez la vente.");
        }

        long retourId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO retour_client (vente_id, motif, utilisateur_id, valide_par, mode, montant_rembourse, session_caisse_id, poste)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.VenteId);
            cmd.Parameters.AddWithValue(a.Motif);
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            cmd.Parameters.AddWithValue(a.ValidePar);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.Mode ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(a.Montant);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)a.SessionCaisseId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            cmd.Parameters.AddWithValue(a.Poste);
            retourId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        var reference = $"Retour client {a.Numero}";
        foreach (var l in a.Lignes)
        {
            await Executer(cx, tx, """
                INSERT INTO retour_client_ligne (retour_id, ligne_vente_id, quantite, destination, montant) VALUES ($1, $2, $3, $4, $5)
                """, ct, retourId, l.LigneVenteId, l.Quantite, l.Destination.Code(), l.Montant);

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

        // Vente à crédit : la dette du patient diminue de la part rendue
        if (a.Mode == "credit" && a.Montant > 0)
            await Executer(cx, tx, """
                UPDATE patient SET solde_credit = GREATEST(0, solde_credit - $2) WHERE id = (SELECT patient_id FROM vente WHERE id = $1)
                """, ct, a.VenteId, a.Montant);

        await tx.CommitAsync(ct);
    }

    private static async Task Executer(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
