using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Achats;

/// <summary>Commandes, réceptions, retours fournisseurs et étiquettes internes (PostgreSQL).</summary>
public sealed class AchatsSql(NpgsqlDataSource db) : IAchats
{
    // ---- Grossistes ---------------------------------------------------------

    public async Task<IReadOnlyList<Fournisseur>> FournisseursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, raison_sociale, telephone, solde, delai_reprise_jours FROM fournisseur ORDER BY raison_sociale");
        return await Lister(cmd, LireFournisseur, ct);
    }

    public async Task<Fournisseur?> FournisseurAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT id, raison_sociale, telephone, solde, delai_reprise_jours FROM fournisseur WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await Lister(cmd, LireFournisseur, ct)).FirstOrDefault();
    }

    private static Fournisseur LireFournisseur(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), Texte(r, 2), r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetInt32(4));

    // ---- Commandes ----------------------------------------------------------

    private const string SelectCommande = """
        SELECT c.id, c.numero, c.fournisseur_id, f.raison_sociale, c.date_commande, c.statut,
               (SELECT count(*) FROM ligne_commande l WHERE l.commande_id = c.id)::int,
               COALESCE((SELECT SUM(l.qte_commandee * l.prix_achat_ht) FROM ligne_commande l WHERE l.commande_id = c.id), 0)
        FROM commande_fournisseur c
        JOIN fournisseur f ON f.id = c.fournisseur_id
        """;

    private static CommandeResume LireCommande(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetDateTime(4), r.GetString(5), r.GetInt32(6), r.GetDecimal(7));

    public async Task<IReadOnlyList<CommandeResume>> CommandesAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectCommande + """

            ORDER BY CASE c.statut WHEN 'brouillon' THEN 0 WHEN 'envoyee' THEN 1 WHEN 'recue_partielle' THEN 1 ELSE 2 END,
                     c.date_commande DESC
            LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limite);
        return await Lister(cmd, LireCommande, ct);
    }

    public async Task<CommandeDetail?> CommandeAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectCommande + " WHERE c.id = $1");
        cmd.Parameters.AddWithValue(id);
        if ((await Lister(cmd, LireCommande, ct)).FirstOrDefault() is not { } entete) return null;

        await using var lignes = db.CreateCommand("""
            SELECT l.id, l.produit_id, p.nom_commercial, l.qte_commandee, l.qte_recue, l.prix_achat_ht
            FROM ligne_commande l JOIN produit p ON p.id = l.produit_id
            WHERE l.commande_id = $1 ORDER BY p.nom_commercial
            """);
        lignes.Parameters.AddWithValue(id);
        return new CommandeDetail(entete, await Lister(lignes, r => new LigneCommandeAchat(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4), r.GetDecimal(5)), ct));
    }

    public async Task<IReadOnlyList<SuggestionCommande>> SuggestionsAsync(CancellationToken ct = default)
    {
        // Dernier achat : le lot reçu le plus récemment pour ce produit
        await using var cmd = db.CreateCommand($"""
            SELECT e.produit_id, e.nom_commercial, e.statut, e.stock::int, e.qte_suggeree::int,
                   d.prix_achat_ht, d.fournisseur_id, d.raison_sociale
            FROM v_etat_stock e
            LEFT JOIN LATERAL (
                SELECT a.prix_achat_ht, a.fournisseur_id, f.raison_sociale
                FROM ({AchatsDuProduit("e.produit_id")}) a JOIN fournisseur f ON f.id = a.fournisseur_id
                ORDER BY a.date_reception DESC, a.ordre DESC LIMIT 1) d ON true
            WHERE e.statut IN ('rupture', 'critique')
            ORDER BY CASE e.statut WHEN 'rupture' THEN 0 ELSE 1 END, e.nom_commercial
            """);
        return await Lister(cmd, r => new SuggestionCommande(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4),
            r.IsDBNull(5) ? null : r.GetDecimal(5), r.IsDBNull(6) ? null : r.GetInt64(6), Texte(r, 7)), ct);
    }

    /// <summary>
    /// Chaque achat d'un produit, avec son prix et sa date : une entrée en stock par livraison (018_lot_unique.sql),
    /// ou le lot lui-même quand il n'a pas de mouvement d'entrée (lots créés avant, données de démonstration).
    /// </summary>
    private static string AchatsDuProduit(string produit) => $"""
        SELECT l.fournisseur_id, COALESCE(m.prix_achat_ht, l.prix_achat_ht) AS prix_achat_ht, r.date_reception, m.id AS ordre
        FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
        JOIN reception r ON r.id = COALESCE(m.reception_id, l.reception_id)
        WHERE l.produit_id = {produit} AND m.type = 'entree'
        UNION ALL
        SELECT l.fournisseur_id, l.prix_achat_ht, r.date_reception, 0
        FROM lot l JOIN reception r ON r.id = l.reception_id
        WHERE l.produit_id = {produit}
          AND NOT EXISTS (SELECT 1 FROM mouvement_stock m WHERE m.lot_id = l.id AND m.type = 'entree')
        """;

    public async Task<DernierAchat?> DernierAchatAsync(long produitId, long fournisseurId, CancellationToken ct = default)
    {
        // Chez ce grossiste d'abord, sinon chez n'importe lequel
        await using var cmd = db.CreateCommand($"""
            SELECT a.prix_achat_ht, a.fournisseur_id
            FROM ({AchatsDuProduit("$1")}) a
            ORDER BY (a.fournisseur_id = $2) DESC, a.date_reception DESC, a.ordre DESC
            LIMIT 1
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(fournisseurId);
        return (await Lister(cmd, r => new DernierAchat(r.GetDecimal(0), r.GetInt64(1)), ct)).FirstOrDefault();
    }

    public async Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_commercial FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<(long Id, string Nom)?> ProduitParGtinAsync(string gtin, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom_commercial FROM produit
            WHERE code_barre = $1 OR (length($1) = 14 AND left($1, 1) = '0' AND code_barre = substr($1, 2))
            ORDER BY actif DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(gtin);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? (r.GetInt64(0), r.GetString(1)) : null;
    }

    public async Task<long> BrouillonAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        // Verrou du grossiste : deux postes ne créent pas deux brouillons en même temps
        await Executer(cnx, tx, "SELECT 1 FROM fournisseur WHERE id = $1 FOR UPDATE", ct, fournisseurId);
        await using (var cherche = new NpgsqlCommand(
            "SELECT id FROM commande_fournisseur WHERE fournisseur_id = $1 AND statut = 'brouillon' ORDER BY id LIMIT 1", cnx, tx))
        {
            cherche.Parameters.AddWithValue(fournisseurId);
            if (await cherche.ExecuteScalarAsync(ct) is long existant)
            {
                await tx.CommitAsync(ct);
                return existant;
            }
        }
        await using var cree = new NpgsqlCommand("""
            INSERT INTO commande_fournisseur (fournisseur_id, numero, utilisateur_id)
            VALUES ($1, prochain_numero_commande(), $2) RETURNING id
            """, cnx, tx);
        cree.Parameters.AddWithValue(fournisseurId);
        cree.Parameters.AddWithValue(utilisateurId);
        var id = (long)(await cree.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task AjouterLigneAsync(long commandeId, long produitId, int quantite, decimal prixHt, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var maj = new NpgsqlCommand("""
            UPDATE ligne_commande SET qte_commandee = qte_commandee + $3, prix_achat_ht = $4
            WHERE commande_id = $1 AND produit_id = $2
            """, cnx, tx))
        {
            maj.Parameters.AddWithValue(commandeId);
            maj.Parameters.AddWithValue(produitId);
            maj.Parameters.AddWithValue(quantite);
            maj.Parameters.AddWithValue(prixHt);
            if (await maj.ExecuteNonQueryAsync(ct) == 0)
                await Executer(cnx, tx, """
                    INSERT INTO ligne_commande (commande_id, produit_id, qte_commandee, prix_achat_ht) VALUES ($1, $2, $3, $4)
                    """, ct, commandeId, produitId, quantite, prixHt);
        }
        await MettreAJourTotal(cnx, tx, commandeId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ModifierLigneAsync(long commandeId, long ligneId, int quantite, decimal prixHt, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await Executer(cnx, tx, "UPDATE ligne_commande SET qte_commandee = $3, prix_achat_ht = $4 WHERE commande_id = $1 AND id = $2",
            ct, commandeId, ligneId, quantite, prixHt);
        await MettreAJourTotal(cnx, tx, commandeId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task SupprimerLigneAsync(long commandeId, long ligneId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await Executer(cnx, tx, "DELETE FROM ligne_commande WHERE commande_id = $1 AND id = $2", ct, commandeId, ligneId);
        await MettreAJourTotal(cnx, tx, commandeId, ct);
        await tx.CommitAsync(ct);
    }

    public async Task ChangerStatutCommandeAsync(long commandeId, string statut, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE commande_fournisseur SET statut = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(commandeId);
        cmd.Parameters.AddWithValue(statut);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static Task MettreAJourTotal(NpgsqlConnection cnx, NpgsqlTransaction tx, long commandeId, CancellationToken ct) =>
        Executer(cnx, tx, """
            UPDATE commande_fournisseur SET montant_total =
                COALESCE((SELECT SUM(qte_commandee * prix_achat_ht) FROM ligne_commande WHERE commande_id = $1), 0)
            WHERE id = $1
            """, ct, commandeId);

    // ---- Réceptions ---------------------------------------------------------

    public async Task<IReadOnlyDictionary<long, decimal>> TauxTvaAsync(IEnumerable<long> produitIds, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, taux_tva FROM produit WHERE id = ANY($1)");
        cmd.Parameters.AddWithValue(produitIds.ToArray());
        var d = new Dictionary<long, decimal>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) d[r.GetInt64(0)] = r.GetDecimal(1);
        return d;
    }

    public async Task<ReceptionEnregistree> ReceptionnerAsync(NouvelleReception n, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);

        long receptionId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO reception (fournisseur_id, commande_id, numero_bl, numero_facture, utilisateur_id, montant_ttc)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(n.FournisseurId);
            cmd.Parameters.Add(Nul(n.CommandeId, NpgsqlDbType.Bigint));
            cmd.Parameters.AddWithValue(n.NumeroBl);
            cmd.Parameters.Add(Nul(n.NumeroFacture, NpgsqlDbType.Text));
            cmd.Parameters.AddWithValue(n.UtilisateurId);
            cmd.Parameters.AddWithValue(n.MontantTtc);
            receptionId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Lignes de la commande, pour rattacher les lots et compter les quantités reçues
        var lignesCommande = new Dictionary<long, long>();
        if (n.CommandeId is { } cid)
        {
            await using var lc = new NpgsqlCommand("SELECT produit_id, id FROM ligne_commande WHERE commande_id = $1 FOR UPDATE", cnx, tx);
            lc.Parameters.AddWithValue(cid);
            await using var r = await lc.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lignesCommande[r.GetInt64(0)] = r.GetInt64(1);
        }

        var lots = new List<LotRecu>();
        foreach (var l in n.Lignes)
        {
            long? ligneCommandeId = lignesCommande.TryGetValue(l.ProduitId, out var lcid) ? lcid : null;
            var numero = Pharma.Domain.Stock.RegleLot.Normaliser(l.NumeroLot);

            // Même n° de lot chez ce grossiste : la livraison complète le lot de même date ; date différente
            // ou lot bloqué selon le choix de l'utilisateur (RegleLot.Decider)
            var memeNumero = await LotsMemeNumeroAsync(cnx, tx, l.ProduitId, n.FournisseurId, numero, verrouiller: true, ct);
            var decision = Pharma.Domain.Stock.RegleLot.Decider(memeNumero, l.DatePeremption,
                new Pharma.Domain.Stock.ChoixLot(l.LotSepare, l.EntrerBloque), l.Nom, numero);

            long lotId;
            if (decision.Completer is { } e)
            {
                // Pas de moyenne : chaque boîte garde le prix de sa livraison, le lot garde celui de sa première livraison
                lotId = e.Id;
                await Executer(cnx, tx, """
                    UPDATE lot SET quantite_disponible = quantite_disponible + $2, date_peremption = $3, date_a_verifier = false
                    WHERE id = $1
                    """, ct, lotId, l.Quantite, l.DatePeremption);
            }
            else
            {
                await using var cmd = new NpgsqlCommand("""
                    INSERT INTO lot (produit_id, ligne_commande_id, numero_lot, date_peremption, quantite_disponible,
                                     prix_achat_ht, reception_id, fournisseur_id, bloque, motif_blocage)
                    VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) RETURNING id
                    """, cnx, tx);
                cmd.Parameters.AddWithValue(l.ProduitId);
                cmd.Parameters.Add(Nul(ligneCommandeId, NpgsqlDbType.Bigint));
                cmd.Parameters.AddWithValue(numero);
                cmd.Parameters.AddWithValue(l.DatePeremption);
                cmd.Parameters.AddWithValue(l.Quantite);
                cmd.Parameters.AddWithValue(l.PrixAchatHt);
                cmd.Parameters.AddWithValue(receptionId);
                cmd.Parameters.AddWithValue(n.FournisseurId);
                cmd.Parameters.AddWithValue(decision.NouveauBloque);
                cmd.Parameters.Add(Nul(decision.NouveauBloque ? "Même n° de lot qu'un lot rappelé : entré bloqué à la réception" : null, NpgsqlDbType.Text));
                lotId = (long)(await cmd.ExecuteScalarAsync(ct))!;
            }
            await Executer(cnx, tx, """
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id, reception_id, prix_achat_ht)
                VALUES ($1, 'entree', $2, $3, $4, $5, $6)
                """, ct, lotId, l.Quantite, $"BL {n.NumeroBl}", n.UtilisateurId, receptionId, l.PrixAchatHt);

            // Boîtes sérialisées (Data Matrix) : identité connue dès la réception
            foreach (var serie in l.NumerosSerie ?? [])
                await Executer(cnx, tx, "INSERT INTO boite (lot_id, gtin, numero_serie, reception_id, prix_achat_ht) VALUES ($1, $2, $3, $4, $5)",
                    ct, lotId, l.Gtin!, serie, receptionId, l.PrixAchatHt);


            // Les autres reçoivent chacune une étiquette interne, dans la même transaction : aucune boîte n'entre sans identité
            var aEtiqueter = l.Quantite - (l.NumerosSerie?.Count ?? 0);
            if (aEtiqueter > 0)
                await Executer(cnx, tx, "SELECT count(*) FROM creer_boites_internes($1, $2, $3, $4)", ct, lotId, aEtiqueter, receptionId, l.PrixAchatHt);

            if (ligneCommandeId is { } id)
                await Executer(cnx, tx, "UPDATE ligne_commande SET qte_recue = qte_recue + $2 WHERE id = $1", ct, id, l.Quantite);

            lots.Add(new LotRecu(lotId, l.Nom, numero, l.DatePeremption, l.Quantite, Math.Max(0, aEtiqueter), decision.Completer is not null));
        }

        if (n.CommandeId is { } commande)
            await Executer(cnx, tx, """
                UPDATE commande_fournisseur SET statut = CASE
                    WHEN EXISTS (SELECT 1 FROM ligne_commande WHERE commande_id = $1 AND qte_recue < qte_commandee)
                    THEN 'recue_partielle' ELSE 'recue' END
                WHERE id = $1
                """, ct, commande);

        await Executer(cnx, tx, "UPDATE fournisseur SET solde = solde + $2 WHERE id = $1", ct, n.FournisseurId, n.MontantTtc);
        await tx.CommitAsync(ct);
        return new ReceptionEnregistree(receptionId, n.MontantTtc, lots);
    }

    public async Task<IReadOnlyList<ReceptionResume>> ReceptionsAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT r.id, f.raison_sociale, r.numero_bl, r.numero_facture, c.numero, r.date_reception,
                   (SELECT count(*) FROM lot l WHERE l.reception_id = r.id OR EXISTS (
                        SELECT 1 FROM mouvement_stock m WHERE m.lot_id = l.id AND m.reception_id = r.id AND m.type = 'entree'))::int,
                   COALESCE((SELECT SUM(m.quantite) FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
                             WHERE COALESCE(m.reception_id, l.reception_id) = r.id AND m.type = 'entree'), 0)::int,
                   r.montant_ttc
            FROM reception r
            JOIN fournisseur f ON f.id = r.fournisseur_id
            LEFT JOIN commande_fournisseur c ON c.id = r.commande_id
            ORDER BY r.date_reception DESC
            LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limite);
        return await Lister(cmd, r => new ReceptionResume(
            r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), Texte(r, 4), r.GetDateTime(5),
            r.GetInt32(6), r.GetInt32(7), r.IsDBNull(8) ? 0 : r.GetDecimal(8)), ct);
    }

    // ---- Retours fournisseurs -----------------------------------------------

    public async Task<LotRetournable?> LotRetournableAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.id, l.produit_id, p.nom_commercial, l.numero_lot, l.date_peremption, l.fournisseur_id,
                   f.raison_sociale, l.quantite_disponible, COALESCE(vl.prix_unitaire, l.prix_achat_ht), l.bloque
            FROM lot l JOIN produit p ON p.id = l.produit_id JOIN fournisseur f ON f.id = l.fournisseur_id
            LEFT JOIN v_valeur_lot vl ON vl.lot_id = l.id
            WHERE l.id = $1
            """);
        cmd.Parameters.AddWithValue(lotId);
        var lot = (await Lister(cmd, r => new LotRetournable(
            r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), r.GetInt64(5),
            r.GetString(6), r.GetInt32(7), r.GetDecimal(8), r.GetBoolean(9), []), ct)).FirstOrDefault();
        if (lot is null) return null;

        await using var boites = db.CreateCommand("""
            SELECT id, COALESCE(code_etiquette, numero_serie) FROM boite
            WHERE lot_id = $1 AND statut = 'en_stock' ORDER BY id
            """);
        boites.Parameters.AddWithValue(lotId);
        return lot with { Boites = await Lister(boites, r => new BoiteEnStock(r.GetInt64(0), r.GetString(1)), ct) };
    }

    public async Task<IReadOnlyList<long>> LotsEnStockAsync(long produitId, string? numeroLot, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id FROM lot WHERE produit_id = $1 AND quantite_disponible > 0 AND ($2::text IS NULL OR numero_lot = upper(btrim($2)))
            ORDER BY date_peremption, id
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.Add(Nul(numeroLot, NpgsqlDbType.Text));
        return await Lister(cmd, r => r.GetInt64(0), ct);
    }

    public async Task<long> CreerRetourAsync(NouveauRetour n, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long id;
        string numero;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO retour_fournisseur (fournisseur_id, numero, motif, montant_attendu, utilisateur_id)
            VALUES ($1, prochain_numero_retour(), $2, $3, $4) RETURNING id, numero
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(n.FournisseurId);
            cmd.Parameters.AddWithValue(n.Motif.Code());
            cmd.Parameters.AddWithValue(n.MontantAttendu);
            cmd.Parameters.AddWithValue(n.UtilisateurId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (id, numero) = (r.GetInt64(0), r.GetString(1));
        }

        foreach (var (lot, quantite, boiteIds) in n.Lignes)
        {
            await Executer(cnx, tx, "INSERT INTO ligne_retour_fournisseur (retour_id, lot_id, quantite) VALUES ($1, $2, $3)",
                ct, id, lot.LotId, quantite);

            await using (var sortie = new NpgsqlCommand("""
                UPDATE lot SET quantite_disponible = quantite_disponible - $2
                WHERE id = $1 AND quantite_disponible >= $2
                """, cnx, tx))
            {
                sortie.Parameters.AddWithValue(lot.LotId);
                sortie.Parameters.AddWithValue(quantite);
                if (await sortie.ExecuteNonQueryAsync(ct) == 0)
                    throw new RegleMetierException($"Lot {lot.NumeroLot} : le stock a changé entre-temps, recommencez.");
            }
            await Executer(cnx, tx, """
                INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
                VALUES ($1, 'retour_fournisseur', $2, $3, $4)
                """, ct, lot.LotId, -quantite, numero, n.UtilisateurId);

            if (boiteIds.Count > 0)
            {
                await using var verif = new NpgsqlCommand("""
                    SELECT count(*) FROM (SELECT 1 FROM boite WHERE id = ANY($1) AND lot_id = $2 AND statut = 'en_stock' FOR UPDATE) b
                    """, cnx, tx);
                verif.Parameters.AddWithValue(boiteIds.ToArray());
                verif.Parameters.AddWithValue(lot.LotId);
                if ((long)(await verif.ExecuteScalarAsync(ct))! != boiteIds.Count)
                    throw new RegleMetierException($"Lot {lot.NumeroLot} : une boîte scannée n’est plus en stock, recommencez.");
                foreach (var b in boiteIds)
                    await Executer(cnx, tx, "SELECT mouvement_boite($1, 'retour_fournisseur', $2, $3, $4)",
                        ct, b, n.UtilisateurId, $"Retour {numero}", id);
            }
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private const string SelectRetour = """
        SELECT r.id, r.numero, r.fournisseur_id, f.raison_sociale, r.motif, r.statut, r.cree_le, r.envoye_le,
               r.avoir_recu_le, COALESCE(r.montant_attendu, 0), r.montant_avoir
        FROM retour_fournisseur r JOIN fournisseur f ON f.id = r.fournisseur_id
        """;

    public async Task<RetourResume?> RetourAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectRetour + " WHERE r.id = $1");
        cmd.Parameters.AddWithValue(id);
        return (await LireRetours(cmd, ct)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<RetourResume>> RetoursAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectRetour + """

            ORDER BY CASE r.statut WHEN 'brouillon' THEN 0 WHEN 'envoye' THEN 1 ELSE 2 END, r.cree_le DESC
            LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limite);
        return await LireRetours(cmd, ct);
    }

    private async Task<IReadOnlyList<RetourResume>> LireRetours(NpgsqlCommand cmd, CancellationToken ct)
    {
        var retours = await Lister(cmd, r => new RetourResume(
            r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetDateTime(6),
            r.IsDBNull(7) ? null : r.GetDateTime(7), r.IsDBNull(8) ? null : r.GetDateTime(8), r.GetDecimal(9),
            r.IsDBNull(10) ? null : r.GetDecimal(10), []), ct);
        if (retours.Count == 0) return retours;

        await using var lignes = db.CreateCommand("""
            SELECT lr.retour_id, p.nom_commercial, l.numero_lot, lr.quantite
            FROM ligne_retour_fournisseur lr JOIN lot l ON l.id = lr.lot_id JOIN produit p ON p.id = l.produit_id
            WHERE lr.retour_id = ANY($1) ORDER BY lr.id
            """);
        lignes.Parameters.AddWithValue(retours.Select(r => r.Id).ToArray());
        var parRetour = (await Lister(lignes, r => (r.GetInt64(0), new LigneRetourAchat(r.GetString(1), r.GetString(2), r.GetInt32(3))), ct))
            .ToLookup(x => x.Item1, x => x.Item2);
        return retours.Select(r => r with { Lignes = parRetour[r.Id].ToList() }).ToList();
    }

    public async Task MarquerEnvoyeAsync(long retourId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE retour_fournisseur SET statut = 'envoye', envoye_le = now() WHERE id = $1 AND statut = 'brouillon'");
        cmd.Parameters.AddWithValue(retourId);
        if (await cmd.ExecuteNonQueryAsync(ct) == 0)
            throw new RegleMetierException("Ce retour a changé entre-temps : rechargez la liste.");
    }

    public async Task EnregistrerAvoirAsync(long retourId, decimal montant, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("""
            UPDATE retour_fournisseur SET statut = 'avoir_recu', montant_avoir = $2, avoir_recu_le = now()
            WHERE id = $1 AND statut = 'envoye' RETURNING fournisseur_id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(retourId);
            cmd.Parameters.AddWithValue(montant);
            if (await cmd.ExecuteScalarAsync(ct) is not long fournisseurId)
                throw new RegleMetierException("Ce retour a changé entre-temps : rechargez la liste.");
            await Executer(cnx, tx, "UPDATE fournisseur SET solde = solde - $2 WHERE id = $1", ct, fournisseurId, montant);
        }
        await tx.CommitAsync(ct);
    }

    public async Task RefuserRetourAsync(long retourId, long utilisateurId, string commentaire, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        string numero;
        await using (var cmd = new NpgsqlCommand("""
            UPDATE retour_fournisseur SET statut = 'refuse' WHERE id = $1 AND statut IN ('brouillon', 'envoye') RETURNING numero
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(retourId);
            numero = await cmd.ExecuteScalarAsync(ct) as string
                ?? throw new RegleMetierException("Ce retour a changé entre-temps : rechargez la liste.");
        }
        // Le stock revient dans chaque lot
        await Executer(cnx, tx, """
            UPDATE lot l SET quantite_disponible = l.quantite_disponible + lr.quantite
            FROM ligne_retour_fournisseur lr WHERE lr.retour_id = $1 AND lr.lot_id = l.id
            """, ct, retourId);
        await Executer(cnx, tx, """
            INSERT INTO mouvement_stock (lot_id, type, quantite, reference, utilisateur_id)
            SELECT lot_id, 'retour_fournisseur', quantite, $2, $3 FROM ligne_retour_fournisseur WHERE retour_id = $1
            """, ct, retourId, numero + " (remise en stock)", utilisateurId);
        // Les boîtes identifiées sorties par ce retour redeviennent « en stock », avec une trace dans leur historique
        await Executer(cnx, tx, """
            WITH remises AS (
                UPDATE boite b SET statut = 'en_stock'
                WHERE b.statut = 'retournee_fournisseur'
                  AND EXISTS (SELECT 1 FROM boite_evenement e
                              WHERE e.boite_id = b.id AND e.type = 'retour_fournisseur' AND e.retour_fournisseur_id = $1)
                RETURNING b.id)
            INSERT INTO boite_evenement (boite_id, type, retour_fournisseur_id, utilisateur_id, commentaire)
            SELECT id, 'correction', $1, $2, $3 FROM remises
            """, ct, retourId, utilisateurId, commentaire);
        await tx.CommitAsync(ct);
    }

    // ---- Étiquettes ---------------------------------------------------------

    public async Task<IReadOnlyList<LotAEtiqueter>> LotsAEtiqueterAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT v.lot_id, v.nom_commercial, v.numero_lot, l.date_peremption, v.quantite_disponible, v.boites_sans_identite::int
            FROM v_lots_a_etiqueter v JOIN lot l ON l.id = v.lot_id
            ORDER BY l.id DESC
            """);
        return await Lister(cmd, r => new LotAEtiqueter(
            r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetInt32(4), r.GetInt32(5)), ct);
    }

    public async Task<IReadOnlyList<EtiquetteBoite>> CreerEtiquettesAsync(long lotId, int nombre, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.nom_commercial, l.numero_lot, l.date_peremption, p.prix_public, f.raison_sociale, c.o_code, e.code
            FROM creer_boites_internes($1, $2) c
            JOIN lot l ON l.id = $1 JOIN produit p ON p.id = l.produit_id JOIN fournisseur f ON f.id = l.fournisseur_id
            LEFT JOIN emplacement e ON e.id = p.emplacement_id
            ORDER BY c.o_code
            """);
        cmd.Parameters.AddWithValue(lotId);
        cmd.Parameters.AddWithValue(nombre);
        return await Lister(cmd, r => new EtiquetteBoite(
            r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6)), ct);
    }

    public async Task<IReadOnlyList<EtiquetteBoite>> EtiquettesReceptionAsync(long receptionId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.nom_commercial, l.numero_lot, l.date_peremption, p.prix_public, f.raison_sociale, b.code_etiquette, e.code
            FROM boite b
            JOIN lot l ON l.id = b.lot_id JOIN produit p ON p.id = l.produit_id JOIN fournisseur f ON f.id = l.fournisseur_id
            LEFT JOIN emplacement e ON e.id = p.emplacement_id
            WHERE b.reception_id = $1 AND b.code_etiquette IS NOT NULL AND b.statut = 'en_stock'
            ORDER BY p.nom_commercial, b.code_etiquette
            """);
        cmd.Parameters.AddWithValue(receptionId);
        return await Lister(cmd, r => new EtiquetteBoite(
            r.GetString(0), r.GetString(1), r.GetFieldValue<DateOnly>(2), r.GetDecimal(3), r.GetString(4), r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6)), ct);
    }

    public async Task<IReadOnlyList<Pharma.Domain.Stock.LotExistant>> LotsMemeNumeroAsync(long produitId, long fournisseurId, string numeroLot, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        return await LotsMemeNumeroAsync(cnx, null, produitId, fournisseurId, Pharma.Domain.Stock.RegleLot.Normaliser(numeroLot), verrouiller: false, ct);
    }

    private static async Task<IReadOnlyList<Pharma.Domain.Stock.LotExistant>> LotsMemeNumeroAsync(NpgsqlConnection cnx, NpgsqlTransaction? tx,
        long produitId, long fournisseurId, string numero, bool verrouiller, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand($"""
            SELECT id, date_peremption, date_a_verifier, quantite_disponible, prix_achat_ht, bloque
            FROM lot WHERE produit_id = $1 AND fournisseur_id = $2 AND numero_lot = $3
            ORDER BY date_peremption {(verrouiller ? "FOR UPDATE" : "")}
            """, cnx, tx);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(fournisseurId);
        cmd.Parameters.AddWithValue(numero);
        return await Lister(cmd, r => new Pharma.Domain.Stock.LotExistant(r.GetInt64(0), r.GetFieldValue<DateOnly>(1), r.GetBoolean(2),
            r.GetInt32(3), r.GetDecimal(4), r.GetBoolean(5)), ct);
    }

    // ---- Outils -------------------------------------------------------------

    private static async Task<IReadOnlyList<T>> Lister<T>(NpgsqlCommand cmd, Func<NpgsqlDataReader, T> lire, CancellationToken ct)
    {
        var liste = new List<T>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(lire(r));
        return liste;
    }

    private static async Task Executer(NpgsqlConnection cnx, NpgsqlTransaction tx, string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cnx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nul<T>(T? valeur, NpgsqlDbType type) =>
        new() { Value = (object?)valeur ?? DBNull.Value, NpgsqlDbType = type };
}
