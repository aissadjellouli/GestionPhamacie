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

    public Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default) => ClasseSurveilleeAsync(db, dci, ct);

    /// <summary>
    /// Classe de la substance surveillée dont le motif figure dans la DCI (majuscules sans accents), stupéfiant avant psychotrope :
    /// même test que la vue v_produit_a_verifier.
    /// </summary>
    internal static async Task<string?> ClasseSurveilleeAsync(NpgsqlDataSource db, string dci, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT s.classe FROM substance_surveillee s
            WHERE majuscule_sans_accent($1) LIKE '%' || s.motif || '%'
            ORDER BY s.classe DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(dci);
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public Task<string?> ClasseNomenclatureAsync(string numEnregistrement, CancellationToken ct = default) => ClasseNomenclatureAsync(db, numEnregistrement, ct);

    /// <summary>
    /// Classe proposée par la nomenclature nationale (v_nomenclature, version la plus récente) pour ce n° d'enregistrement, seulement
    /// si elle est sous registre ; stupéfiant avant psychotrope si plusieurs lignes portent ce numéro.
    /// </summary>
    internal static async Task<string?> ClasseNomenclatureAsync(NpgsqlDataSource db, string numEnregistrement, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT n.classe_proposee FROM v_nomenclature n
            WHERE n.num_enregistrement = $1 AND n.classe_proposee IN ('psychotrope', 'stupefiant')
              AND n.version_id = (SELECT max(x.version_id) FROM nomenclature_produit x WHERE x.num_enregistrement = $1)
            ORDER BY n.classe_proposee DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(numEnregistrement.Trim());
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    public async Task<long> CreerAsync(FicheProduit f, bool aVerifier, CancellationToken ct = default)
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
                                 remboursable, taux_tva, classe, stock_minimum, num_enregistrement, categorie, liste_psychotrope, a_verifier)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15) RETURNING id
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
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.StockMinimum ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.NumEnregistrement ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(f.Categorie);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.ListePsychotrope ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(aVerifier);
        long id;
        try { id = (long)(await cmd.ExecuteScalarAsync(ct))!; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation && e.ConstraintName == "fk_produit_categorie")
        {
            throw new Pharma.Domain.Commun.RegleMetierException("Catégorie inconnue : choisissez-en une de la liste.", "categorie");
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName?.Contains("num_enregistrement") == true)
        {
            throw new Pharma.Domain.Commun.RegleMetierException($"Le n° d'enregistrement {f.NumEnregistrement} est déjà celui d'un produit du catalogue.");
        }
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
