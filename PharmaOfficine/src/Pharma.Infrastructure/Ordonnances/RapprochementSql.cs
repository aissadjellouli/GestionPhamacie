using Npgsql;
using Pharma.Application.Ordonnances;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Ordonnances;

/// <summary>
/// Rapprochement tolérant aux fautes d'OCR (pg_trgm) : le nom commercial ou la DCI doit se retrouver dans la ligne lue
/// (word_similarity), avec un bonus quand le dosage du produit y figure aussi. À score égal, le nom complet le plus
/// ressemblant passe devant (ex. « Metformine 850 mg (Labo A) » prescrit, même en rupture : l'équivalent sera proposé ensuite).
/// </summary>
public sealed class RapprochementSql(NpgsqlDataSource db) : IRapprochementCatalogue
{
    private const string Sql = """
        WITH c AS (
            SELECT p.id, p.nom_commercial, d.nom AS dci, p.dosage, p.forme, p.prix_public,
                   COALESCE(e.stock, 0)::int AS stock, p.classe,
                   LEAST(1.0, GREATEST(
                       word_similarity(lower(split_part(p.nom_commercial, ' ', 1)), lower($1)),
                       word_similarity(lower(p.nom_commercial), lower($1)),
                       COALESCE(word_similarity(lower(d.nom), lower($1)), 0))
                     + CASE WHEN p.dosage IS NOT NULL
                             AND position(lower(replace(p.dosage, ' ', '')) IN lower(replace($1, ' ', ''))) > 0
                            THEN 0.15 ELSE 0 END) AS score,
                   word_similarity(lower(p.nom_commercial), lower($1)) AS ressemblance_nom
            FROM produit p
            LEFT JOIN dci d          ON d.id = p.dci_id
            LEFT JOIN v_etat_stock e ON e.produit_id = p.id
            WHERE p.actif
        )
        SELECT id, nom_commercial, dci, dosage, forme, prix_public, stock, classe, score::float8
        FROM c
        WHERE score >= $3
        ORDER BY score DESC, ressemblance_nom DESC, stock DESC, nom_commercial
        LIMIT $2
        """;

    public async Task<IReadOnlyList<ProduitCandidat>> RapprocherAsync(string texte, int limite, double scoreMinimal, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(Sql);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        cmd.Parameters.AddWithValue(scoreMinimal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ProduitCandidat>();
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitCandidat(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetDecimal(5), r.GetInt32(6),
                CodesMetier.ClasseDepuisCode(r.GetString(7)), Math.Round(r.GetDouble(8), 2)));
        return liste;
    }
}
