using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Remises automatiques (table remise_automatique) : pourcentages du jour pour la caisse, et gestion par le titulaire.</summary>
public sealed class RemisesAutomatiquesSql(NpgsqlDataSource db) : IRemisesAutomatiques, IRemisesAutomatiquesAdmin
{
    /// <summary>
    /// Pourcentage du jour de chaque produit non remboursable : remise du produit, sinon de sa catégorie (remises non arrêtées dont la
    /// période contient le jour). Les produits sans remise sont absents.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, decimal>> PourcentagesAsync(IReadOnlyCollection<long> produitIds, DateOnly jour, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id,
                   COALESCE((SELECT ra.pourcentage FROM remise_automatique ra
                             WHERE ra.produit_id = p.id AND ra.arretee_le IS NULL AND $2 BETWEEN ra.date_debut AND ra.date_fin
                             ORDER BY ra.id DESC LIMIT 1),
                            (SELECT ra.pourcentage FROM remise_automatique ra
                             WHERE ra.categorie = p.categorie AND ra.arretee_le IS NULL AND $2 BETWEEN ra.date_debut AND ra.date_fin
                             ORDER BY ra.id DESC LIMIT 1))
            FROM produit p
            WHERE p.id = ANY($1) AND NOT p.remboursable
            """);
        cmd.Parameters.AddWithValue(produitIds.ToArray());
        cmd.Parameters.AddWithValue(jour);
        var resultat = new Dictionary<long, decimal>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            if (!r.IsDBNull(1)) resultat[r.GetInt64(0)] = r.GetDecimal(1);
        return resultat;
    }

    private const string Selection = """
        SELECT ra.id, ra.produit_id, p.nom_commercial, ra.categorie, c.libelle, ra.pourcentage, ra.date_debut, ra.date_fin, ra.motif,
               uc.nom_complet, ra.cree_le, ra.arretee_le, ua.nom_complet
        FROM remise_automatique ra
        LEFT JOIN produit p ON p.id = ra.produit_id
        LEFT JOIN categorie_produit c ON c.code = ra.categorie
        JOIN utilisateur uc ON uc.id = ra.cree_par
        LEFT JOIN utilisateur ua ON ua.id = ra.arretee_par
        """;

    private static RemiseAutomatique Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.IsDBNull(1) ? null : r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5), r.GetFieldValue<DateOnly>(6), r.GetFieldValue<DateOnly>(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.GetString(9), r.GetDateTime(10), r.IsDBNull(11) ? null : r.GetDateTime(11),
        r.IsDBNull(12) ? null : r.GetString(12));

    public async Task<IReadOnlyList<RemiseAutomatique>> ListerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Selection + " ORDER BY ra.arretee_le IS NOT NULL, ra.date_fin < current_date, ra.date_debut DESC, ra.id DESC");
        var liste = new List<RemiseAutomatique>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public async Task<RemiseAutomatique?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Selection + " WHERE ra.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<ProduitRemise?> ProduitAsync(long produitId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT nom_commercial, remboursable FROM produit WHERE id = $1");
        cmd.Parameters.AddWithValue(produitId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ProduitRemise(r.GetString(0), r.GetBoolean(1)) : null;
    }

    public async Task<string?> CategorieAsync(string code, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT libelle FROM categorie_produit WHERE code = $1");
        cmd.Parameters.AddWithValue(code);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<bool> ChevaucheAsync(RemiseAutomatiqueSaisie s, long? saufId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (
                SELECT 1 FROM remise_automatique ra
                WHERE ra.arretee_le IS NULL
                  AND ra.id IS DISTINCT FROM $1
                  AND (ra.produit_id = $2 OR ra.categorie = $3)
                  AND ra.date_debut <= $5 AND ra.date_fin >= $4)
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)saufId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.ProduitId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.Categorie ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(s.Debut);
        cmd.Parameters.AddWithValue(s.Fin);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static void Saisie(NpgsqlCommand cmd, RemiseAutomatiqueSaisie s)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.ProduitId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.Categorie ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(s.Pourcentage);
        cmd.Parameters.AddWithValue(s.Debut);
        cmd.Parameters.AddWithValue(s.Fin);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.Motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    public async Task<long> CreerAsync(RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO remise_automatique (produit_id, categorie, pourcentage, date_debut, date_fin, motif, cree_par)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        Saisie(cmd, s);
        cmd.Parameters.AddWithValue(auteurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task ModifierAsync(long id, RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE remise_automatique
            SET produit_id = $1, categorie = $2, pourcentage = $3, date_debut = $4, date_fin = $5, motif = $6, modifiee_par = $7, modifiee_le = now()
            WHERE id = $8 AND arretee_le IS NULL
            """);
        Saisie(cmd, s);
        cmd.Parameters.AddWithValue(auteurId);
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ArreterAsync(long id, long auteurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE remise_automatique SET arretee_le = now(), arretee_par = $2 WHERE id = $1 AND arretee_le IS NULL");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(auteurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
