using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Catégories de produits (045) et classement d'un produit : catégorie, liste d'un psychotrope.</summary>
public sealed class ClassementProduitsSql(NpgsqlDataSource db) : IClassementProduits
{
    public async Task<IReadOnlyList<CategorieProduit>> CategoriesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT code, libelle, medicament FROM categorie_produit WHERE actif ORDER BY ordre, libelle");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<CategorieProduit>();
        while (await r.ReadAsync(ct)) liste.Add(new CategorieProduit(r.GetString(0), r.GetString(1), r.GetBoolean(2)));
        return liste;
    }

    public async Task<ClassementProduit?> LireAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom_commercial, p.classe, p.categorie, p.liste_psychotrope,
                   p.num_enregistrement IS NOT NULL
                       AND EXISTS (SELECT 1 FROM nomenclature_produit n WHERE n.num_enregistrement = btrim(p.num_enregistrement)),
                   EXISTS (SELECT 1 FROM substance_surveillee s JOIN dci d ON d.id = p.dci_id
                           WHERE majuscule_sans_accent(d.nom) LIKE '%' || s.motif || '%')
            FROM produit p WHERE p.id = $1
            """);
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ClassementProduit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetBoolean(5), r.GetBoolean(6))
            : null;
    }

    public async Task<bool> ModifierAsync(long produitId, string categorie, string? listePsychotrope, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET categorie = $2, liste_psychotrope = $3 WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(categorie);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)listePsychotrope ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>
    /// Produits à classer : actifs, catégorie médicament, classe libre, n° d'enregistrement vide ou absent de la nomenclature (toutes
    /// versions), sans substance surveillée dans le nom ni la DCI (ceux-là restent des médicaments). Recherche sans majuscules ni accents.
    /// </summary>
    public async Task<Page<ProduitAClasser>> AClasserAsync(string? texte, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT p.id, p.nom_commercial, d.nom, p.dosage, p.forme, p.num_enregistrement, p.remboursable, p.tarif_reference, p.prix_public,
                   COALESCE((SELECT sum(l.quantite_disponible) FROM lot l WHERE l.produit_id = p.id), 0)::int,
                   {PaginationSql.Total}
            FROM produit p
            JOIN categorie_produit c ON c.code = p.categorie AND c.medicament
            LEFT JOIN dci d ON d.id = p.dci_id
            WHERE p.actif AND p.classe = 'libre'
              AND (NULLIF(btrim(p.num_enregistrement), '') IS NULL
                   OR NOT EXISTS (SELECT 1 FROM nomenclature_produit n WHERE n.num_enregistrement = btrim(p.num_enregistrement)))
              AND NOT EXISTS (SELECT 1 FROM substance_surveillee s
                              WHERE majuscule_sans_accent(p.nom_commercial || ' ' || COALESCE(d.nom, '')) LIKE '%' || s.motif || '%')
              AND ($1::text IS NULL
                   OR strpos(majuscule_sans_accent(p.nom_commercial || ' ' || COALESCE(d.nom, '') || ' ' || COALESCE(p.num_enregistrement, '')),
                             majuscule_sans_accent($1)) > 0)
            ORDER BY p.nom_commercial, p.id
            {PaginationSql.Limite(2)}
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        PaginationSql.Parametres(cmd, page);
        string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return await PaginationSql.LireAsync(cmd, page, 10, r => new ProduitAClasser(r.GetInt64(0), r.GetString(1), T(r, 2), T(r, 3), T(r, 4), T(r, 5),
            r.GetBoolean(6), r.IsDBNull(7) ? null : r.GetDecimal(7), r.GetDecimal(8), r.GetInt32(9)), ct);
    }
}
