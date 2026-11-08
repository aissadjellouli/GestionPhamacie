using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Catalogue;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

public sealed class CatalogueEcritureSql(NpgsqlDataSource db) : ICatalogueEcriture
{
    public async Task<bool> CodeBarreExisteAsync(string codeBarre, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM produit WHERE code_barre = $1)");
        cmd.Parameters.AddWithValue(codeBarre);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<string?> DoublonAsync(string nom, string dosage, string forme, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT nom_commercial FROM produit
            WHERE lower(nom_commercial) = lower($1) AND lower(COALESCE(dosage, '')) = lower($2) AND lower(COALESCE(forme, '')) = lower($3)
            """);
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.AddWithValue(dosage);
        cmd.Parameters.AddWithValue(forme);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<long> CreerAsync(FicheProduit f, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        long dciId;
        await using (var dci = new NpgsqlCommand("""
            WITH existante AS (SELECT id FROM dci WHERE lower(nom) = lower($1)),
                 creee AS (INSERT INTO dci (nom) SELECT $1 WHERE NOT EXISTS (SELECT 1 FROM existante) RETURNING id)
            SELECT id FROM existante UNION ALL SELECT id FROM creee
            """, cnx, tx))
        {
            dci.Parameters.AddWithValue(f.Dci);
            dciId = (long)(await dci.ExecuteScalarAsync(ct))!;
        }

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO produit (code_barre, nom_commercial, dci_id, dosage, forme, prix_public, tarif_reference,
                                 remboursable, taux_tva, classe, stock_minimum)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11) RETURNING id
            """, cnx, tx);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.CodeBarre ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(f.NomCommercial);
        cmd.Parameters.AddWithValue(dciId);
        cmd.Parameters.AddWithValue(f.Dosage);
        cmd.Parameters.AddWithValue(f.Forme);
        cmd.Parameters.AddWithValue(f.PrixPublic);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.TarifReference ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });
        cmd.Parameters.AddWithValue(f.Remboursable);
        cmd.Parameters.AddWithValue(f.TauxTva);
        cmd.Parameters.AddWithValue(f.Classe.Code());
        cmd.Parameters.AddWithValue(f.StockMinimum);
        var id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT nom FROM dci WHERE nom ILIKE '%' || $1 || '%' OR similarity(nom, $1) > 0.3
            ORDER BY similarity(nom, $1) DESC, nom LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<string>();
        while (await r.ReadAsync(ct)) liste.Add(r.GetString(0));
        return liste;
    }
}
