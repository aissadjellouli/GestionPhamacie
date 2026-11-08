using Npgsql;
using Pharma.Application.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

public sealed class CatalogueLecture(NpgsqlDataSource db) : ICatalogueLecture
{
    // Accents ignorés des deux côtés : « paracet » trouve « Paracétamol » (saisie rapide au comptoir)
    private const string SansAccent = "'àâäáãçéèêëíìîïñóòôöõúùûüÿœÀÂÄÁÃÇÉÈÊËÍÌÎÏÑÓÒÔÖÕÚÙÛÜŸŒ', 'aaaaaceeeeiiiinooooouuuuyoAAAAACEEEEIIIINOOOOOUUUUYO'";

    // Code-barres exact, puis nom ou DCI, tolérante aux fautes (pg_trgm)
    private const string SqlRecherche = $$"""
        WITH q AS (SELECT translate($1, {{SansAccent}}) AS t)
        SELECT p.id, p.nom_commercial, d.nom, p.dosage, p.forme, p.prix_public,
               COALESCE(e.stock, 0)::int, p.classe
        FROM produit p
        CROSS JOIN q
        LEFT JOIN dci d          ON d.id = p.dci_id
        LEFT JOIN v_etat_stock e ON e.produit_id = p.id
        WHERE p.actif
          AND (p.code_barre = $1
               OR translate(p.nom_commercial, {{SansAccent}}) ILIKE '%' || q.t || '%'
               OR translate(d.nom, {{SansAccent}}) ILIKE '%' || q.t || '%'
               OR similarity(translate(p.nom_commercial, {{SansAccent}}), q.t) > 0.3)
        ORDER BY COALESCE(p.code_barre = $1, false) DESC,
                 similarity(translate(p.nom_commercial, {{SansAccent}}), q.t) DESC, p.nom_commercial
        LIMIT $2
        """;

    public async Task<IReadOnlyList<ProduitResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SqlRecherche);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ProduitResume>();
        while (await r.ReadAsync(ct))
            liste.Add(new ProduitResume(
                r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4),
                r.GetDecimal(5), r.GetInt32(6), CodesMetier.ClasseDepuisCode(r.GetString(7))));
        return liste;
    }
}
