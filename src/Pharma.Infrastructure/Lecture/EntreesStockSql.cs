using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Entrées de stock (lots) et leurs corrections tracées (table correction_lot, script 103).</summary>
public sealed class EntreesStockSql(NpgsqlDataSource db, Pharma.Application.Securite.AutorisationCourante? autorisation = null) : IEntreesStock
{
    private const string Colonnes = """
        SELECT l.id, l.produit_id, p.nom_commercial, l.numero_lot, l.date_peremption, l.quantite_disponible, l.ppa, l.shp,
               l.fournisseur_id, f.raison_sociale, r.date_reception, l.bloque,
               (SELECT count(*) FROM correction_lot c WHERE c.lot_id = l.id)::int,
               (SELECT count(*) FROM boite b WHERE b.lot_id = l.id AND b.statut = 'en_stock')::int
        """;

    private const string Tables = """
        FROM lot l
        JOIN produit p ON p.id = l.produit_id
        JOIN fournisseur f ON f.id = l.fournisseur_id
        JOIN reception r ON r.id = l.reception_id
        """;

    private const string Filtre = """
        WHERE ($1::boolean OR l.quantite_disponible > 0)
          AND ($2::text IS NULL OR p.nom_commercial ILIKE '%' || $2 || '%' OR l.numero_lot ILIKE '%' || $2 || '%'
               OR p.code_barre = $2)
        """;

    private static EntreeStock Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetInt32(5),
        r.IsDBNull(6) ? null : r.GetDecimal(6), r.GetDecimal(7), r.GetInt64(8), r.GetString(9), r.GetDateTime(10), r.GetBoolean(11),
        r.GetInt32(12), r.GetInt32(13));

    private static void Parametres(NpgsqlCommand cmd, FiltreEntreesStock f)
    {
        cmd.Parameters.AddWithValue(f.AvecEpuises);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    public async Task<Page<EntreeStock>> PageAsync(FiltreEntreesStock filtre, DemandePage page, CancellationToken ct = default)
    {
        var lignes = new List<EntreeStock>();
        await using (var cmd = db.CreateCommand(Colonnes + " " + Tables + " " + Filtre + " ORDER BY p.nom_commercial, l.date_peremption, l.id LIMIT $3 OFFSET $4"))
        {
            Parametres(cmd, filtre);
            cmd.Parameters.AddWithValue(page.Taille);
            cmd.Parameters.AddWithValue(page.Decalage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lignes.Add(Lire(r));
        }
        await using var compte = db.CreateCommand("SELECT count(*)::int " + Tables + " " + Filtre);
        Parametres(compte, filtre);
        return new Page<EntreeStock>(lignes, (int)(await compte.ExecuteScalarAsync(ct))!, page.Page, page.Taille);
    }

    public async Task<EntreeStock?> LotAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Colonnes + " " + Tables + " WHERE l.id = $1");
        cmd.Parameters.AddWithValue(lotId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<bool> LotIdentiqueExisteAsync(long lotId, long produitId, long fournisseurId, string numeroLot, DateOnly peremption,
        CancellationToken ct = default)
    {
        // Même comparaison que la contrainte ux_lot_fournisseur_date (n° normalisé par le déclencheur trg_lot_numero)
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM lot WHERE id <> $1 AND produit_id = $2 AND fournisseur_id = $3 AND numero_lot = $4 AND date_peremption = $5)
            """);
        cmd.Parameters.AddWithValue(lotId);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(fournisseurId);
        cmd.Parameters.AddWithValue(numeroLot);
        cmd.Parameters.AddWithValue(peremption);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<CorrectionLot>> CorrectionsAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.id, c.champ, c.ancienne_valeur, c.nouvelle_valeur, c.motif, c.corrige_le, u.nom_complet
            FROM correction_lot c JOIN utilisateur u ON u.id = c.utilisateur_id
            WHERE c.lot_id = $1
            ORDER BY c.corrige_le DESC, c.id DESC
            """);
        cmd.Parameters.AddWithValue(lotId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<CorrectionLot>();
        while (await r.ReadAsync(ct))
            liste.Add(new CorrectionLot(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.GetFieldValue<DateTimeOffset>(5), r.GetString(6)));
        return liste;
    }

    public async Task CorrigerAsync(CorrectionAEcrire c, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Le lot n'est corrigé que si sa quantité n'a pas bougé depuis la lecture (vente ou sortie sur un autre poste)
        await using (var maj = new NpgsqlCommand("""
            UPDATE lot SET ppa = COALESCE($2, ppa), numero_lot = COALESCE($3, numero_lot),
                           date_peremption = COALESCE($4, date_peremption),
                           date_a_verifier = CASE WHEN $4 IS NULL THEN date_a_verifier ELSE false END,
                           shp = COALESCE($5, shp), quantite_disponible = COALESCE($6, quantite_disponible)
            WHERE id = $1 AND quantite_disponible = $7
            """, cx, tx))
        {
            maj.Parameters.AddWithValue(c.LotId);
            maj.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Ppa ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
            maj.Parameters.Add(new NpgsqlParameter { Value = (object?)c.NumeroLot ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            maj.Parameters.Add(new NpgsqlParameter { Value = (object?)c.DatePeremption ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
            maj.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Shp ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
            maj.Parameters.Add(new NpgsqlParameter { Value = (object?)c.Quantite ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
            maj.Parameters.AddWithValue(c.QuantiteAvant);
            if (await maj.ExecuteNonQueryAsync(ct) == 0)
                throw new RegleMetierException("Le stock de ce lot vient de changer (vente ou sortie sur un autre poste) : rechargez la liste et recommencez.");
        }

        // Lot dont le n° est rappelé : bloqué comme à la réception (motif tracé)
        if (c.BloquerLot)
        {
            await using var blocage = new NpgsqlCommand(
                "UPDATE lot SET bloque = true, motif_blocage = 'Même n° de lot qu''un lot rappelé : bloqué à la correction d''entrée' WHERE id = $1", cx, tx);
            blocage.Parameters.AddWithValue(c.LotId);
            await blocage.ExecuteNonQueryAsync(ct);
        }

        // Boîtes étiquetées encore en stock (lot verrouillé par l'UPDATE ci-dessus) : la quantité ne descend pas en dessous
        if (c.Quantite is { } nouvelle)
        {
            await using var controle = new NpgsqlCommand("SELECT count(*)::int FROM boite WHERE lot_id = $1 AND statut = 'en_stock'", cx, tx);
            controle.Parameters.AddWithValue(c.LotId);
            var enStock = (int)(await controle.ExecuteScalarAsync(ct))!;
            if (nouvelle < enStock)
                throw new RegleMetierException(
                    $"{enStock} boîte(s) étiquetée(s) de ce lot sont en stock : retirez d’abord les boîtes absentes (Sortie de stock ou inventaire).", "quantite");
        }

        // Quantité : mouvement d'ajustement (le registre des psychotropes l'inscrit en écart, par le déclencheur)
        long? mouvementId = null;
        if (c.Quantite is { } q && q != c.QuantiteAvant)
        {
            await using var mvt = new NpgsqlCommand("""
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
                VALUES ($1, 'ajustement_inventaire', $2, $3, $4) RETURNING id
                """, cx, tx);
            mvt.Parameters.AddWithValue(c.LotId);
            mvt.Parameters.AddWithValue(q - c.QuantiteAvant);
            mvt.Parameters.AddWithValue("Correction d'entrée : " + c.Motif);
            mvt.Parameters.AddWithValue(c.UtilisateurId);
            mouvementId = (long)(await mvt.ExecuteScalarAsync(ct))!;
        }

        foreach (var champ in c.Champs)
        {
            await using var j = new NpgsqlCommand("""
                INSERT INTO correction_lot (lot_id, champ, ancienne_valeur, nouvelle_valeur, motif, mouvement_id, utilisateur_id)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """, cx, tx);
            j.Parameters.AddWithValue(c.LotId);
            j.Parameters.AddWithValue(champ.Champ);
            j.Parameters.Add(new NpgsqlParameter { Value = (object?)champ.AncienneValeur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            j.Parameters.Add(new NpgsqlParameter { Value = (object?)champ.NouvelleValeur ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            j.Parameters.AddWithValue(c.Motif);
            j.Parameters.Add(new NpgsqlParameter { Value = champ.Champ == "quantite" ? (object?)mouvementId ?? DBNull.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            j.Parameters.AddWithValue(c.UtilisateurId);
            await j.ExecuteNonQueryAsync(ct);
        }
        if (c.Journal is { } ecrit)
            await Securite.JournalSql.EcrireAsync(cx, tx, c.UtilisateurId, ecrit.Action, ecrit.TableCible, ecrit.EnregistrementId, ecrit.Details, ecrit.PermissionCode, ct,
                autorisation?.AutoriseParId);
        await tx.CommitAsync(ct);
    }

    public async Task<bool> NumeroRappeleAsync(long lotId, long produitId, string numeroLot, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM lot WHERE produit_id = $1 AND numero_lot = $2 AND bloque AND id <> $3)");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(numeroLot);
        cmd.Parameters.AddWithValue(lotId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
