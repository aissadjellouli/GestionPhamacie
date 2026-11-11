using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;

namespace Pharma.Infrastructure.Lecture;

/// <summary>Blocage des produits à l'achat et à la vente (046).</summary>
public sealed class BlocageProduitsSql(NpgsqlDataSource db) : IBlocageProduits
{
    private const string Colonnes = "id, nom_commercial, bloque_achat, bloque_vente, motif_blocage";

    private static BlocageProduit Lire(NpgsqlDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetBoolean(2), r.GetBoolean(3), r.IsDBNull(4) ? null : r.GetString(4));

    public async Task<BlocageProduit?> LireAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<IReadOnlyList<BlocageProduit>> BloquesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM produit WHERE bloque_achat OR bloque_vente ORDER BY nom_commercial");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<BlocageProduit>();
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<bool> ModifierAsync(long produitId, bool bloqueAchat, bool bloqueVente, string? motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE produit SET bloque_achat = $2, bloque_vente = $3, motif_blocage = $4 WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(bloqueAchat);
        cmd.Parameters.AddWithValue(bloqueVente);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
