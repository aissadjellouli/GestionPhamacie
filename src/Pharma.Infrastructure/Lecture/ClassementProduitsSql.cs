using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;

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
        await using var cmd = db.CreateCommand("SELECT id, nom_commercial, classe, categorie, liste_psychotrope FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ClassementProduit(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4))
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
}
