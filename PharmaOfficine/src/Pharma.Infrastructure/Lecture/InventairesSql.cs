using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Inventaire tournant. Un lot appartient au rayon de l'emplacement de vente de son produit
/// (à défaut, de sa réserve, sinon « (Sans emplacement) ») : on compte toutes ses boîtes, réserve comprise, pour éviter les faux écarts.
/// </summary>
public sealed class InventairesSql(NpgsqlDataSource db) : IInventaires
{
    private const string LotsParRayon = """
        SELECT COALESCE(ev.rayon, er.rayon, '(Sans emplacement)') AS rayon, l.id AS lot_id, l.date_a_verifier
        FROM lot l
        JOIN produit p ON p.id = l.produit_id
        LEFT JOIN emplacement ev ON ev.id = p.emplacement_id
        LEFT JOIN emplacement er ON er.id = p.reserve_id
        WHERE l.quantite_disponible > 0
        """;

    public async Task<ParametresInventaire> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT seuil_boites, seuil_montant FROM parametre_inventaire WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) throw new InvalidOperationException("Réglages d'inventaire absents : exécutez database/017_inventaire.sql.");
        return new ParametresInventaire(r.GetInt32(0), r.GetDecimal(1));
    }

    public async Task ModifierParametresAsync(ParametresInventaire p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_inventaire SET seuil_boites = $1, seuil_montant = $2 WHERE id = 1");
        cmd.Parameters.AddWithValue(p.SeuilBoites);
        cmd.Parameters.AddWithValue(p.SeuilMontant);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RayonInventaire>> RayonsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            WITH lots AS ({LotsParRayon}),
                 rayons AS (SELECT DISTINCT rayon FROM emplacement WHERE actif
                            UNION SELECT DISTINCT rayon FROM lots WHERE rayon = '(Sans emplacement)')
            SELECT r.rayon,
                   (SELECT max(i.termine_le) FROM inventaire i WHERE i.rayon = r.rayon AND i.statut = 'termine'),
                   (SELECT count(*) FROM lots WHERE lots.rayon = r.rayon)::int,
                   (SELECT count(*) FROM lots WHERE lots.rayon = r.rayon AND lots.date_a_verifier)::int,
                   (SELECT i.id FROM inventaire i WHERE i.rayon = r.rayon AND i.statut = 'en_cours')
            FROM rayons r
            ORDER BY r.rayon
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<RayonInventaire>();
        while (await r.ReadAsync(ct))
            liste.Add(new RayonInventaire(r.GetString(0), r.IsDBNull(1) ? null : r.GetDateTime(1), r.GetInt32(2), r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetInt64(4)));
        return liste;
    }

    public async Task<long> CreerAsync(string rayon, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long id;
        // Pendant un inventaire complet, le rayon compté lui est rattaché
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO inventaire (rayon, commence_par, campagne_id)
            VALUES ($1, $2, (SELECT id FROM campagne_inventaire WHERE statut = 'en_cours')) RETURNING id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(rayon);
            cmd.Parameters.AddWithValue(utilisateurId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await using (var cmd = new NpgsqlCommand($"""
            INSERT INTO ligne_inventaire (inventaire_id, lot_id)
            SELECT $1, lot_id FROM ({LotsParRayon}) lots WHERE lots.rayon = $2
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(rayon);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<InventaireDetail?> DetailAsync(long id, CancellationToken ct = default)
    {
        InventaireDetail? entete = null;
        await using (var cmd = db.CreateCommand("""
            SELECT i.id, i.rayon, i.statut, i.commence_le, u.nom_complet, i.termine_le
            FROM inventaire i JOIN utilisateur u ON u.id = i.commence_par WHERE i.id = $1
            """))
        {
            cmd.Parameters.AddWithValue(id);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            entete = new InventaireDetail(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5), []);
        }

        await using var lignes = db.CreateCommand("""
            SELECT li.id, l.id, p.nom_commercial, l.numero_lot, l.date_peremption, l.date_a_verifier,
                   concat_ws(' + réserve ', ev.code, er.code), COALESCE(vl.prix_unitaire, l.prix_achat_ht), li.compte, li.date_verifiee, li.statut,
                   li.theorique, li.ecart, li.valeur_ecart
            FROM ligne_inventaire li
            JOIN lot l ON l.id = li.lot_id
            LEFT JOIN v_valeur_lot vl ON vl.lot_id = l.id
            JOIN produit p ON p.id = l.produit_id
            LEFT JOIN emplacement ev ON ev.id = p.emplacement_id
            LEFT JOIN emplacement er ON er.id = p.reserve_id
            WHERE li.inventaire_id = $1
            ORDER BY l.date_a_verifier DESC, ev.code NULLS LAST, p.nom_commercial, l.date_peremption
            """);
        lignes.Parameters.AddWithValue(id);
        await using var rl = await lignes.ExecuteReaderAsync(ct);
        var liste = new List<LigneInventaire>();
        while (await rl.ReadAsync(ct))
            liste.Add(new LigneInventaire(rl.GetInt64(0), rl.GetInt64(1), rl.GetString(2), rl.GetString(3), rl.GetFieldValue<DateOnly>(4),
                rl.GetBoolean(5), rl.IsDBNull(6) || rl.GetString(6) == "" ? null : rl.GetString(6), rl.GetDecimal(7),
                rl.IsDBNull(8) ? null : rl.GetInt32(8), rl.IsDBNull(9) ? null : rl.GetFieldValue<DateOnly>(9), rl.GetString(10),
                rl.IsDBNull(11) ? null : rl.GetInt32(11), rl.IsDBNull(12) ? null : rl.GetInt32(12), rl.IsDBNull(13) ? null : rl.GetDecimal(13)));
        return entete with { Lignes = liste };
    }

    public async Task EnregistrerComptageAsync(long ligneId, int compte, DateOnly? dateVerifiee, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE ligne_inventaire li
            SET compte = $2, theorique = l.quantite_disponible, date_verifiee = COALESCE($3, li.date_verifiee),
                compte_par = $4, compte_le = now(), statut = 'compte'
            FROM lot l
            WHERE li.id = $1 AND l.id = li.lot_id
            """);
        cmd.Parameters.AddWithValue(ligneId);
        cmd.Parameters.AddWithValue(compte);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)dateVerifiee ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long?> LotParCodeAsync(string code, CancellationToken ct = default)
    {
        // Étiquette interne de boîte ou code interne du lot
        await using (var cmd = db.CreateCommand("SELECT lot_id FROM rechercher_par_code($1) WHERE source <> 'code_fabricant' LIMIT 1"))
        {
            cmd.Parameters.AddWithValue(code);
            if (await cmd.ExecuteScalarAsync(ct) is long lot) return lot;
        }
        // Data Matrix : GTIN et numéro de lot
        if (!Gs1DataMatrix.TryParse(code, out var dm) || dm?.Lot is null) return null;
        await using var lotDm = db.CreateCommand("""
            SELECT l.id FROM lot l JOIN produit p ON p.id = l.produit_id
            WHERE (p.code_barre = $1 OR (length($1) = 14 AND left($1, 1) = '0' AND p.code_barre = substr($1, 2)))
              AND upper(l.numero_lot) = upper($2)
            ORDER BY l.quantite_disponible DESC, l.id LIMIT 1
            """);
        lotDm.Parameters.AddWithValue(dm.Gtin);
        lotDm.Parameters.AddWithValue(dm.Lot);
        return await lotDm.ExecuteScalarAsync(ct) as long?;
    }

    public async Task TerminerAsync(long id, IReadOnlyList<DecisionLigne> decisions, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        string rayon;
        await using (var cmd = new NpgsqlCommand("SELECT rayon FROM inventaire WHERE id = $1 AND statut = 'en_cours' FOR UPDATE", cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            rayon = await cmd.ExecuteScalarAsync(ct) as string ?? throw new RegleMetierException("Cet inventaire est déjà terminé.");
        }

        foreach (var d in decisions)
        {
            var statut = d.Decision switch { DecisionEcart.Conforme => "conforme", DecisionEcart.Ajuste => "ajuste", _ => "a_valider" };
            await using (var cmd = new NpgsqlCommand(
                "UPDATE ligne_inventaire SET ecart = $2, valeur_ecart = $3, statut = $4 WHERE id = $1", cnx, tx))
            {
                cmd.Parameters.AddWithValue(d.LigneId);
                cmd.Parameters.AddWithValue(d.Ecart);
                cmd.Parameters.AddWithValue(d.ValeurEcart);
                cmd.Parameters.AddWithValue(statut);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            if (d.Decision == DecisionEcart.Ajuste)
                await Ajuster(cnx, tx, d.LigneId, d.Ecart, $"Inventaire n° {id} (rayon {rayon})", utilisateurId, ct);
        }

        // Dates confirmées pendant le comptage : le lot n'est plus « date à vérifier »
        await using (var cmd = new NpgsqlCommand("""
            UPDATE lot l SET date_peremption = li.date_verifiee, date_a_verifier = false
            FROM ligne_inventaire li WHERE li.inventaire_id = $1 AND li.lot_id = l.id AND li.date_verifiee IS NOT NULL
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand(
            "UPDATE inventaire SET statut = 'termine', termine_le = now(), termine_par = $2 WHERE id = $1", cnx, tx))
        {
            cmd.Parameters.AddWithValue(id);
            cmd.Parameters.AddWithValue(utilisateurId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    /// <summary>Ajoute l'écart au stock ACTUEL du lot (des ventes ont pu avoir lieu depuis le comptage) et trace le mouvement.</summary>
    private static async Task Ajuster(NpgsqlConnection cnx, NpgsqlTransaction tx, long ligneId, int ecart, string reference, long utilisateurId, CancellationToken ct)
    {
        // 1. Stock actuel, ligne verrouillée jusqu'à la fin de la transaction
        long lotId;
        int avant;
        await using (var lire = new NpgsqlCommand("""
            SELECT l.id, l.quantite_disponible FROM lot l JOIN ligne_inventaire li ON li.lot_id = l.id
            WHERE li.id = $1 FOR UPDATE OF l
            """, cnx, tx))
        {
            lire.Parameters.AddWithValue(ligneId);
            await using var r = await lire.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return;
            (lotId, avant) = (r.GetInt64(0), r.GetInt32(1));
        }
        var apres = Math.Max(0, avant + ecart);
        if (apres == avant) return;

        // 2. Nouveau stock, 3. mouvement tracé avec l'écart réellement appliqué
        await using (var maj = new NpgsqlCommand("UPDATE lot SET quantite_disponible = $2 WHERE id = $1", cnx, tx))
        {
            maj.Parameters.AddWithValue(lotId);
            maj.Parameters.AddWithValue(apres);
            await maj.ExecuteNonQueryAsync(ct);
        }
        await using var mvt = new NpgsqlCommand("""
            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id) VALUES ($1, 'ajustement_inventaire', $2, $3, $4)
            """, cnx, tx);
        mvt.Parameters.AddWithValue(lotId);
        mvt.Parameters.AddWithValue(apres - avant);
        mvt.Parameters.AddWithValue(reference);
        mvt.Parameters.AddWithValue(utilisateurId);
        await mvt.ExecuteNonQueryAsync(ct);
    }

    public async Task AnnulerAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE inventaire SET statut = 'annule', termine_le = now() WHERE id = $1 AND statut = 'en_cours'");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<EcartAValider>> EcartsAValiderAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT li.id, i.id, i.rayon, p.nom_commercial, l.numero_lot, li.theorique, li.compte, li.ecart, li.valeur_ecart,
                   u.nom_complet, li.compte_le
            FROM ligne_inventaire li
            JOIN inventaire i ON i.id = li.inventaire_id
            JOIN lot l ON l.id = li.lot_id
            JOIN produit p ON p.id = l.produit_id
            JOIN utilisateur u ON u.id = li.compte_par
            WHERE li.statut = 'a_valider'
            ORDER BY abs(li.valeur_ecart) DESC
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<EcartAValider>();
        while (await r.ReadAsync(ct))
            liste.Add(new EcartAValider(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5),
                r.GetInt32(6), r.GetInt32(7), r.GetDecimal(8), r.GetString(9), r.GetDateTime(10)));
        return liste;
    }

    public async Task DeciderAsync(long ligneId, bool valider, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        int ecart;
        long inventaireId;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE ligne_inventaire SET statut = $2, decide_par = $3, decide_le = now()
            WHERE id = $1 AND statut = 'a_valider' RETURNING ecart, inventaire_id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(ligneId);
            cmd.Parameters.AddWithValue(valider ? "valide" : "refuse");
            cmd.Parameters.AddWithValue(utilisateurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) throw new RegleMetierException("Cet écart a déjà été traité.");
            (ecart, inventaireId) = (r.GetInt32(0), r.GetInt64(1));
        }
        if (valider)
            await Ajuster(cnx, tx, ligneId, ecart, $"Inventaire n° {inventaireId}, écart validé par le titulaire", utilisateurId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<InventaireResume>> HistoriqueAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT i.id, i.rayon, i.statut, i.commence_le, u.nom_complet, i.termine_le,
                   count(li.id)::int,
                   count(li.id) FILTER (WHERE li.ecart <> 0)::int,
                   count(li.id) FILTER (WHERE li.statut = 'a_valider')::int,
                   COALESCE(sum(li.valeur_ecart), 0)
            FROM inventaire i JOIN utilisateur u ON u.id = i.commence_par
            LEFT JOIN ligne_inventaire li ON li.inventaire_id = i.id
            GROUP BY i.id, u.nom_complet
            ORDER BY i.commence_le DESC LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<InventaireResume>();
        while (await r.ReadAsync(ct))
            liste.Add(new InventaireResume(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDateTime(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5), r.GetInt32(6), r.GetInt32(7), r.GetInt32(8), r.GetDecimal(9)));
        return liste;
    }
}
