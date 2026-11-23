using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Domain.Catalogue;

namespace Pharma.Infrastructure.Catalogue;

/// <summary>Lecture du fichier officiel : feuilles reconnues par leur en-tête (023_nomenclature.sql pour le stockage).</summary>
public sealed class LecteurNomenclatureXlsx : ILecteurNomenclature
{
    public NomenclatureLue Lire(Stream contenu)
    {
        var feuilles = LecteurXlsx.Lire(contenu);
        var lignes = new List<LigneNomenclature>();
        string? libelle = null;
        foreach (var (nom, cellules) in feuilles)
        {
            var lues = Nomenclature.Lire(nom, cellules);
            if (lues.Count == 0) continue;
            if (libelle is null && Nomenclature.SituationDeFeuille(nom) == SituationNomenclature.EnVigueur) libelle = nom.Trim();
            lignes.AddRange(lues);
        }
        return new NomenclatureLue(libelle ?? "Nomenclature", lignes);
    }
}

public sealed class NomenclatureSql(NpgsqlDataSource db) : INomenclature
{
    private static string Code(SituationNomenclature s) => s switch
    {
        SituationNomenclature.Retire => "retire",
        SituationNomenclature.NonRenouvele => "non_renouvele",
        _ => "en_vigueur"
    };

    public async Task<long> RemplacerAsync(NomenclatureLue n, string fichier, long utilisateurId, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);

        long versionId;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO nomenclature_version (libelle, fichier, en_vigueur, non_renouveles, retraits, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """, cnx, tx))
        {
            cmd.Parameters.AddWithValue(n.Libelle);
            cmd.Parameters.AddWithValue(fichier);
            cmd.Parameters.AddWithValue(n.Lignes.Count(l => l.Situation == SituationNomenclature.EnVigueur));
            cmd.Parameters.AddWithValue(n.Lignes.Count(l => l.Situation == SituationNomenclature.NonRenouvele));
            cmd.Parameters.AddWithValue(n.Lignes.Count(l => l.Situation == SituationNomenclature.Retire));
            cmd.Parameters.AddWithValue(utilisateurId);
            versionId = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }

        // Seule la dernière version est gardée ; l'historique des imports reste dans nomenclature_version
        await using (var cmd = new NpgsqlCommand("DELETE FROM nomenclature_produit", cnx, tx))
            await cmd.ExecuteNonQueryAsync(ct);

        await using (var copie = await cnx.BeginBinaryImportAsync("""
            COPY nomenclature_produit (version_id, situation, num_enregistrement, code, dci, marque, forme, dosage, conditionnement,
                                       liste, p1, p2, obs, laboratoire, pays, date_initiale, date_finale, type, statut, stabilite)
            FROM STDIN (FORMAT BINARY)
            """, ct))
        {
            foreach (var l in n.Lignes)
            {
                await copie.StartRowAsync(ct);
                await copie.WriteAsync(versionId, NpgsqlDbType.Bigint, ct);
                await copie.WriteAsync(Code(l.Situation), NpgsqlDbType.Text, ct);
                foreach (var t in new[] { l.NumEnregistrement, l.Code, l.Dci, l.Marque, l.Forme, l.Dosage, l.Conditionnement,
                                          l.Liste, l.P1, l.P2, l.Obs, l.Laboratoire, l.Pays })
                    await Texte(copie, t, ct);
                await Date(copie, l.DateInitiale, ct);
                await Date(copie, l.DateFinale, ct);
                await Texte(copie, l.Type, ct);
                await Texte(copie, l.Statut, ct);
                await Texte(copie, l.Stabilite, ct);
            }
            await copie.CompleteAsync(ct);
        }

        await tx.CommitAsync(ct);
        return versionId;
    }

    private static Task Texte(NpgsqlBinaryImporter c, string? v, CancellationToken ct) =>
        v is null ? c.WriteNullAsync(ct) : c.WriteAsync(v.Length > 500 ? v[..500] : v, NpgsqlDbType.Text, ct);

    private static Task Date(NpgsqlBinaryImporter c, DateOnly? v, CancellationToken ct) =>
        v is { } d ? c.WriteAsync(d, NpgsqlDbType.Date, ct) : c.WriteNullAsync(ct);

    public async Task<VersionNomenclature?> VersionAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT v.libelle, v.fichier, v.en_vigueur, v.non_renouveles, v.retraits, v.importee_le, u.nom_complet
            FROM nomenclature_version v JOIN utilisateur u ON u.id = v.utilisateur_id
            ORDER BY v.id DESC LIMIT 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new VersionNomenclature(r.GetString(0), r.GetString(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4),
            r.GetDateTime(5), r.GetString(6));
    }

    public async Task<IReadOnlyList<MedicamentNomenclature>> RechercherAsync(string texte, int limite, CancellationToken ct = default)
    {
        // Nom de marque ou DCI (tolérant aux fautes), ou n° d'enregistrement exact ; en vigueur d'abord
        await using var cmd = db.CreateCommand("""
            SELECT n.num_enregistrement, n.code, n.dci, n.marque, n.forme, n.dosage, n.conditionnement, n.liste,
                   n.laboratoire, n.pays, n.situation, n.classe_proposee,
                   EXISTS (SELECT 1 FROM produit p WHERE p.num_enregistrement = n.num_enregistrement)
            FROM v_nomenclature n
            WHERE n.marque ILIKE '%' || $1 || '%' OR n.dci ILIKE '%' || $1 || '%'
               OR n.num_enregistrement = $1 OR similarity(n.marque, $1) > 0.35
            ORDER BY n.situation = 'en_vigueur' DESC,
                     (n.marque ILIKE $1 || '%') DESC, similarity(n.marque, $1) DESC, n.marque, n.dosage
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        var liste = new List<MedicamentNomenclature>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (await r.ReadAsync(ct))
            liste.Add(new MedicamentNomenclature(S(0), S(1), S(2), S(3), S(4), S(5), S(6), S(7), S(8), S(9),
                r.GetString(10), r.GetString(11), r.GetBoolean(12)));
        return liste;
    }

    public async Task<IReadOnlyList<ProduitAVerifier>> AVerifierAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT produit_id, nom_commercial, dosage, forme, dci, num_enregistrement, classe, classe_proposee, situation, motif
            FROM v_produit_a_verifier ORDER BY motif, nom_commercial
            """);
        var liste = new List<ProduitAVerifier>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitAVerifier(r.GetInt64(0), r.GetString(1), S(2), S(3), S(4), S(5), r.GetString(6), S(7), S(8), r.GetString(9)));
        return liste;
    }

    public async Task<string?> ChangerClasseAsync(long produitId, string classe, long utilisateurId, CancellationToken ct = default, bool confirmation = false)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        var synchrone = await Pharma.Infrastructure.SecondServeur.ReplicationSynchrone.ActiverAsync(cx, tx, ct);
        string? ancienne;
        // Confirmation du titulaire (classe moins stricte que la substance ou la nomenclature) mémorisée (080) ; sinon effacée
        await using (var cmd = new NpgsqlCommand("""
            UPDATE produit p SET classe = $2, a_verifier = false,
                                 classe_confirmee_par = CASE WHEN $4 THEN $3 END, classe_confirmee_le = CASE WHEN $4 THEN now() END
            FROM (SELECT classe FROM produit WHERE id = $1 FOR UPDATE) avant
            WHERE p.id = $1 RETURNING avant.classe
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(produitId);
            cmd.Parameters.AddWithValue(classe);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(confirmation);
            ancienne = (string?)await cmd.ExecuteScalarAsync(ct);
        }
        if (ancienne is null) return null;

        // Le produit entre sous registre (psychotrope, stupéfiant) avec du stock : solde d'ouverture par lot, comme au
        // démarrage du registre (024), sinon la première délivrance donnerait un solde négatif. Une seule fois par produit.
        if (classe is "psychotrope" or "stupefiant") await InscrireOuvertureAsync(cx, tx, produitId, utilisateurId, ct);
        await Pharma.Infrastructure.SecondServeur.ReplicationSynchrone.ValiderAsync(db, cx, tx, synchrone, ct);
        return ancienne;
    }

    public async Task<string?> ClasseSurveilleeProduitAsync(long produitId, CancellationToken ct = default)
    {
        string? dci, num;
        await using (var cmd = db.CreateCommand("SELECT d.nom, p.num_enregistrement FROM produit p LEFT JOIN dci d ON d.id = p.dci_id WHERE p.id = $1"))
        {
            cmd.Parameters.AddWithValue(produitId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct)) return null;
            dci = r.IsDBNull(0) ? null : r.GetString(0);
            num = r.IsDBNull(1) ? null : r.GetString(1);
        }
        // Mêmes sources qu'à la création et à la modification d'un produit (CatalogueEcritureSql) : la plus stricte des deux
        var parDci = dci is null ? null : await Lecture.CatalogueEcritureSql.ClasseSurveilleeAsync(db, dci, ct);
        var parNomenclature = string.IsNullOrWhiteSpace(num) ? null : await Lecture.CatalogueEcritureSql.ClasseNomenclatureAsync(db, num, ct);
        return Pharma.Domain.Catalogue.RegleProduit.ClasseSurveilleeRetenue(parDci, parNomenclature);
    }

    /// <summary>
    /// Produit qui entre sous registre avec du stock : solde d'ouverture par lot, une seule fois par produit
    /// (aussi pour une classe imposée à la modification de la fiche).
    /// </summary>
    internal static async Task InscrireOuvertureAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long produitId, long utilisateurId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference)
            SELECT l.id, 'ouverture', l.quantite_disponible, l.quantite_disponible, $2, 'Solde d''ouverture : produit classé sous registre'
            FROM lot l
            WHERE l.produit_id = $1 AND l.quantite_disponible > 0
              AND NOT EXISTS (SELECT 1 FROM registre_psychotrope r JOIN lot l2 ON l2.id = r.lot_id
                              WHERE l2.produit_id = $1 AND r.type_mouvement = 'ouverture')
            ORDER BY l.id
            """, cx, tx);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
