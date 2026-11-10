using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Annulation d'une vente (027_annulation_vente.sql) : tout en une transaction.</summary>
public sealed class AnnulationsVenteSql(NpgsqlDataSource db) : IAnnulationsVente
{
    private const string Select = """
        SELECT v.id, v.numero, v.date_vente, u.nom_complet, NULLIF(trim(concat_ws(' ', pa.nom, pa.prenom)), ''),
               v.montant_total, v.part_patient, v.part_organisme,
               (SELECT p.mode FROM paiement p WHERE p.vente_id = v.id ORDER BY p.id LIMIT 1),
               v.statut, COALESCE(s.fermee_le IS NULL, false), v.bordereau_id IS NOT NULL,
               -- Déjà rendu par des retours (argent ou dette diminuée), dette actuelle du patient, facture en vigueur
               (SELECT COALESCE(SUM(rc.montant_rembourse), 0) FROM retour_client rc WHERE rc.vente_id = v.id),
               COALESCE(pa.solde_credit, 0),
               (SELECT fc.numero FROM facture_client_ligne fl JOIN facture_client fc ON fc.id = fl.facture_id
                WHERE fl.vente_id = v.id AND fc.type = 'facture' AND fc.statut IN ('emise', 'rejetee') ORDER BY fc.id LIMIT 1)
        FROM vente v
        JOIN utilisateur u ON u.id = v.utilisateur_id
        LEFT JOIN patient pa ON pa.id = v.patient_id
        LEFT JOIN session_caisse s ON s.id = v.session_caisse_id
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

    private async Task<IReadOnlyList<VenteAnnulable>> LireAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        var ventes = new List<(long Id, VenteAnnulable V)>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                ventes.Add((r.GetInt64(0), new VenteAnnulable(r.GetInt64(0), r.GetString(1), r.GetDateTime(2), r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5), r.GetDecimal(6), r.GetDecimal(7),
                    r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetBoolean(10), r.GetBoolean(11), [],
                    r.GetDecimal(12), r.GetDecimal(13), r.IsDBNull(14) ? null : r.GetString(14))));
        if (ventes.Count == 0) return [];

        var lignes = new Dictionary<long, List<LigneVenteAnnulable>>();
        await using (var c = db.CreateCommand("""
            SELECT l.vente_id, p.nom_commercial, l.quantite, l.prix_unitaire * l.quantite
            FROM ligne_vente l JOIN produit p ON p.id = l.produit_id WHERE l.vente_id = ANY($1) ORDER BY l.id
            """))
        {
            c.Parameters.AddWithValue(ventes.Select(v => v.Id).ToArray());
            await using var r = await c.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (!lignes.TryGetValue(r.GetInt64(0), out var l)) lignes[r.GetInt64(0)] = l = [];
                l.Add(new LigneVenteAnnulable(r.GetString(1), r.GetInt32(2), r.GetDecimal(3)));
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

    public async Task AnnulerAsync(AnnulationAEcrire a, CancellationToken ct = default)
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

        // 4. Vente à crédit : la dette diminue de ce qui reste dû (calculé avant) ; si elle a bougé entre-temps, on refuse
        if (a.ReductionDette > 0 && patientId is { } patient)
        {
            await using var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx);
            cmd.Parameters.AddWithValue(patient);
            cmd.Parameters.AddWithValue(a.ReductionDette);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("La dette du patient vient de changer : rechargez la vente avant d’annuler.");
        }

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
    }
}
