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

    public async Task<string?> ChangerClasseAsync(long produitId, string classe, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE produit p SET classe = $2 FROM (SELECT classe FROM produit WHERE id = $1 FOR UPDATE) avant
            WHERE p.id = $1 RETURNING avant.classe
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(classe);
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }
}
