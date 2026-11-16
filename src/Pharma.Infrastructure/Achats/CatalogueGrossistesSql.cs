using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Achats;

/// <summary>Profils de fichier, imports et dernier état connu (prix, disponibilité, délai) par produit et grossiste (115).</summary>
public sealed class CatalogueGrossistesSql(NpgsqlDataSource db) : ICatalogueGrossistes
{
    public async Task<ProfilCatalogue?> ProfilAsync(long fournisseurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT colonne_code, colonne_code_barre, colonne_designation, colonne_prix_ht, colonne_disponibilite, colonne_delai,
                   valeurs_indisponible, valeurs_disponible, modifie_le
            FROM profil_catalogue_grossiste WHERE fournisseur_id = $1
            """);
        cmd.Parameters.AddWithValue(fournisseurId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new ProfilCatalogue(fournisseurId, Texte(r, 0), Texte(r, 1), Texte(r, 2), Texte(r, 3), Texte(r, 4), Texte(r, 5), Texte(r, 6), Texte(r, 7),
            r.GetFieldValue<DateTime>(8));
    }

    public async Task EnregistrerProfilAsync(ProfilCatalogue p, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO profil_catalogue_grossiste (fournisseur_id, colonne_code, colonne_code_barre, colonne_designation, colonne_prix_ht,
                colonne_disponibilite, colonne_delai, valeurs_indisponible, valeurs_disponible, modifie_le, modifie_par)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, now(), $10)
            ON CONFLICT (fournisseur_id) DO UPDATE SET
                colonne_code = EXCLUDED.colonne_code, colonne_code_barre = EXCLUDED.colonne_code_barre,
                colonne_designation = EXCLUDED.colonne_designation, colonne_prix_ht = EXCLUDED.colonne_prix_ht,
                colonne_disponibilite = EXCLUDED.colonne_disponibilite, colonne_delai = EXCLUDED.colonne_delai,
                valeurs_indisponible = EXCLUDED.valeurs_indisponible, valeurs_disponible = EXCLUDED.valeurs_disponible,
                modifie_le = now(), modifie_par = EXCLUDED.modifie_par
            """);
        cmd.Parameters.AddWithValue(p.FournisseurId);
        foreach (var v in new[] { p.ColonneCode, p.ColonneCodeBarre, p.ColonneDesignation, p.ColonnePrixHt, p.ColonneDisponibilite, p.ColonneDelai,
                     p.ValeursIndisponible, p.ValeursDisponible })
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyDictionary<string, long>> ProduitsParCodeBarreAsync(IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        var res = new Dictionary<string, long>();
        if (codes.Count == 0) return res;
        // Code-barres du produit, ou GTIN-14 commençant par 0 (même règle que la lecture DataMatrix) ; produit actif d'abord
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT ON (c) c, p.id
            FROM unnest($1::text[]) AS c
            JOIN produit p ON p.code_barre = c OR (length(c) = 14 AND left(c, 1) = '0' AND p.code_barre = substr(c, 2))
            ORDER BY c, p.actif DESC, p.id
            """);
        cmd.Parameters.AddWithValue(codes.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) res[r.GetString(0)] = r.GetInt64(1);
        return res;
    }

    public async Task<IReadOnlyDictionary<string, long>> ProduitsParCodeGrossisteAsync(long fournisseurId, IReadOnlyCollection<string> codes, CancellationToken ct = default)
    {
        var res = new Dictionary<string, long>();
        if (codes.Count == 0) return res;
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT ON (code_grossiste) code_grossiste, produit_id
            FROM catalogue_grossiste WHERE fournisseur_id = $1 AND code_grossiste = ANY($2)
            ORDER BY code_grossiste, maj_le DESC
            """);
        cmd.Parameters.AddWithValue(fournisseurId);
        cmd.Parameters.AddWithValue(codes.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) res[r.GetString(0)] = r.GetInt64(1);
        return res;
    }

    public async Task<long> EnregistrerImportAsync(long fournisseurId, string nomFichier, long utilisateurId, int lignesLues,
        IReadOnlyList<LigneCatalogueRapprochee> lignes, int lignesIgnorees, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO import_catalogue_grossiste (fournisseur_id, nom_fichier, utilisateur_id, lignes_lues, lignes_rapprochees, lignes_ignorees)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(nomFichier);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(lignesLues);
            cmd.Parameters.AddWithValue(lignes.Count);
            cmd.Parameters.AddWithValue(lignesIgnorees);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        if (lignes.Count > 0)
        {
            // Dernier état connu remplacé ; le code du grossiste déjà connu est gardé si la nouvelle ligne n'en a pas
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO catalogue_grossiste (fournisseur_id, produit_id, code_grossiste, designation, prix_ht, disponible,
                    quantite_disponible, delai_jours, import_id, maj_le)
                SELECT $1, t.p, t.c, t.d, t.px, t.di, t.q, t.de, $9, now()
                FROM unnest($2::bigint[], $3::text[], $4::text[], $5::numeric[], $6::boolean[], $7::int[], $8::int[]) AS t(p, c, d, px, di, q, de)
                ON CONFLICT (fournisseur_id, produit_id) DO UPDATE SET
                    code_grossiste = COALESCE(EXCLUDED.code_grossiste, catalogue_grossiste.code_grossiste),
                    designation = EXCLUDED.designation, prix_ht = EXCLUDED.prix_ht, disponible = EXCLUDED.disponible,
                    quantite_disponible = EXCLUDED.quantite_disponible, delai_jours = EXCLUDED.delai_jours,
                    import_id = EXCLUDED.import_id, maj_le = now()
                """, cnx, tx);
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(lignes.Select(l => l.ProduitId).ToArray());
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.Code).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.Designation).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.PrixHt).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Numeric });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.Disponible).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Boolean });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.QuantiteDisponible).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            cmd.Parameters.Add(new NpgsqlParameter { Value = lignes.Select(l => l.Ligne.DelaiJours).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer });
            cmd.Parameters.AddWithValue(id);
            await cmd.ExecuteNonQueryAsync(ct);
            await NoterRupturesAsync(cnx, tx, fournisseurId, id, lignes, ct);
        }
        await tx.CommitAsync(ct);
        return id;
    }

    /// <summary>
    /// Historique des ruptures (116), dans la transaction de l'import : passages disponible ⇄ indisponible selon
    /// <see cref="RegleHistoriqueRuptures"/> (rupture ouverte ou terminée, avec l'import qui l'a constaté).
    /// </summary>
    private static async Task NoterRupturesAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, long fournisseurId, long importId,
        IReadOnlyList<LigneCatalogueRapprochee> lignes, CancellationToken ct)
    {
        var ouvertes = new HashSet<long>();
        await using (var cmd = new NpgsqlCommand(
            "SELECT produit_id FROM rupture_grossiste WHERE fournisseur_id = $1 AND fin IS NULL AND produit_id = ANY($2) FOR UPDATE", cnx, tx))
        {
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(lignes.Select(l => l.ProduitId).ToArray());
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ouvertes.Add(r.GetInt64(0));
        }
        var transitions = lignes.Select(l => (l.ProduitId, T: RegleHistoriqueRuptures.Transition(ouvertes.Contains(l.ProduitId), l.Ligne.Disponible))).ToList();
        var aOuvrir = transitions.Where(t => t.T == TransitionRupture.Ouvrir).Select(t => t.ProduitId).Distinct().ToArray();
        var aFermer = transitions.Where(t => t.T == TransitionRupture.Fermer).Select(t => t.ProduitId).Distinct().ToArray();
        if (aOuvrir.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO rupture_grossiste (fournisseur_id, produit_id, debut, import_debut_id)
                SELECT $1, p, now(), $3 FROM unnest($2::bigint[]) AS p
                ON CONFLICT DO NOTHING
                """, cnx, tx);
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(aOuvrir);
            cmd.Parameters.AddWithValue(importId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (aFermer.Length > 0)
        {
            await using var cmd = new NpgsqlCommand("""
                UPDATE rupture_grossiste SET fin = now(), import_fin_id = $3
                WHERE fournisseur_id = $1 AND fin IS NULL AND produit_id = ANY($2)
                """, cnx, tx);
            cmd.Parameters.AddWithValue(fournisseurId);
            cmd.Parameters.AddWithValue(aFermer);
            cmd.Parameters.AddWithValue(importId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<Page<ImportCatalogue>> ImportsAsync(long? fournisseurId, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT i.id, i.fournisseur_id, f.raison_sociale, i.nom_fichier, i.importe_le, u.nom_complet,
                   i.lignes_lues, i.lignes_rapprochees, i.lignes_ignorees, count(*) OVER ()
            FROM import_catalogue_grossiste i
            JOIN fournisseur f ON f.id = i.fournisseur_id
            LEFT JOIN utilisateur u ON u.id = i.utilisateur_id
            WHERE $1::bigint IS NULL OR i.fournisseur_id = $1
            ORDER BY i.importe_le DESC, i.id DESC
            LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)fournisseurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<ImportCatalogue>();
        var total = 0;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
            {
                lignes.Add(new ImportCatalogue(r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateTime>(4), Texte(r, 5),
                    r.GetInt32(6), r.GetInt32(7), r.GetInt32(8)));
                total = (int)r.GetInt64(9);
            }
        if (lignes.Count == 0 && page.Page > 1)
        {
            await using var c = db.CreateCommand("SELECT count(*) FROM import_catalogue_grossiste WHERE $1::bigint IS NULL OR fournisseur_id = $1");
            c.Parameters.Add(new NpgsqlParameter { Value = (object?)fournisseurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
            total = (int)(long)(await c.ExecuteScalarAsync(ct))!;
        }
        return new Page<ImportCatalogue>(lignes, total, page.Page, page.Taille);
    }

    public async Task<IReadOnlyList<OffreGrossiste>> OffresAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default)
    {
        if (produitIds.Count == 0) return [];
        // Dernier achat par produit et grossiste : même définition que AchatsSql (entrée en stock par livraison, net de remise,
        // unités gratuites à 0 DA ignorées)
        await using var cmd = db.CreateCommand("""
            WITH achats AS (
                SELECT DISTINCT ON (x.produit_id, x.fournisseur_id) x.produit_id, x.fournisseur_id, x.prix_achat_ht, x.date_reception
                FROM (
                    SELECT l.produit_id, l.fournisseur_id, COALESCE(m.prix_achat_ht, l.prix_achat_ht) AS prix_achat_ht, r.date_reception, m.id AS ordre
                    FROM mouvement_stock m JOIN lot l ON l.id = m.lot_id
                    JOIN reception r ON r.id = COALESCE(m.reception_id, l.reception_id)
                    WHERE l.produit_id = ANY($1) AND m.type = 'entree' AND COALESCE(m.prix_achat_ht, l.prix_achat_ht) > 0
                    UNION ALL
                    SELECT l.produit_id, l.fournisseur_id, l.prix_achat_ht, r.date_reception, 0
                    FROM lot l JOIN reception r ON r.id = l.reception_id
                    WHERE l.produit_id = ANY($1) AND l.prix_achat_ht > 0
                      AND NOT EXISTS (SELECT 1 FROM mouvement_stock m WHERE m.lot_id = l.id AND m.type = 'entree')
                ) x
                WHERE x.fournisseur_id IS NOT NULL
                ORDER BY x.produit_id, x.fournisseur_id, x.date_reception DESC, x.ordre DESC
            ),
            cat AS (SELECT * FROM catalogue_grossiste WHERE produit_id = ANY($1))
            SELECT COALESCE(c.produit_id, a.produit_id), f.id, f.raison_sociale, c.prix_ht, c.disponible, c.quantite_disponible, c.delai_jours,
                   c.maj_le, a.prix_achat_ht, a.date_reception
            FROM cat c
            FULL JOIN achats a ON a.produit_id = c.produit_id AND a.fournisseur_id = c.fournisseur_id
            JOIN fournisseur f ON f.id = COALESCE(c.fournisseur_id, a.fournisseur_id)
            WHERE f.actif
            """);
        cmd.Parameters.AddWithValue(produitIds.ToArray());
        var res = new List<OffreGrossiste>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            res.Add(new OffreGrossiste(r.GetInt64(0), r.GetInt64(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetDecimal(3), r.IsDBNull(4) ? null : r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetInt32(5),
                r.IsDBNull(6) ? null : r.GetInt32(6), r.IsDBNull(7) ? null : r.GetFieldValue<DateTime>(7),
                r.IsDBNull(8) ? null : r.GetDecimal(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTime>(9)));
        return res;
    }

    public async Task MarquerEnvoiEmailAsync(long commandeId, string adresse, DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE commande_fournisseur SET envoyee_email_le = $2, envoyee_email_a = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(commandeId);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(quandUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(adresse);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
