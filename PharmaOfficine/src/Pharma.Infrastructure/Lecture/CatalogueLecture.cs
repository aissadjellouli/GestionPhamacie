using Npgsql;
using Pharma.Application.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Lecture;

public sealed class CatalogueLecture(NpgsqlDataSource db) : ICatalogueLecture
{
    // Accents ignorés des deux côtés : « paracet » trouve « Paracétamol » (saisie rapide au comptoir).
    // sans_accent() (033_index_performance.sql) est indexée en trigrammes sur le nom et la DCI :
    // chaque condition passe par un index, au lieu de recalculer tous les produits à chaque frappe.
    // Tolérance aux fautes : opérateur % de pg_trgm (similarité au moins 0,3, réglage par défaut).
    private const string SqlRecherche = """
        WITH q AS (SELECT sans_accent($1) AS t),
             trouves AS (
                SELECT id FROM produit WHERE code_barre = $1
                UNION SELECT p.id FROM produit p, q WHERE sans_accent(p.nom_commercial) ILIKE '%' || q.t || '%'
                UNION SELECT p.id FROM produit p, q WHERE sans_accent(p.nom_commercial) % q.t
                UNION SELECT p.id FROM produit p JOIN dci d ON d.id = p.dci_id, q WHERE sans_accent(d.nom) ILIKE '%' || q.t || '%')
        SELECT p.id, p.nom_commercial, d.nom, p.dosage, p.forme, p.prix_public,
               COALESCE(e.stock, 0)::int, p.classe
        FROM trouves t
        JOIN produit p ON p.id = t.id
        CROSS JOIN q
        LEFT JOIN dci d          ON d.id = p.dci_id
        -- Stock vendable (même calcul que v_etat_stock : lots non périmés), lu pour ce produit seulement
        LEFT JOIN LATERAL (SELECT sum(l.quantite_disponible) AS stock FROM lot l
                           WHERE l.produit_id = p.id AND l.date_peremption > current_date) e ON true
        WHERE p.actif
        ORDER BY COALESCE(p.code_barre = $1, false) DESC,
                 similarity(sans_accent(p.nom_commercial), q.t) DESC, p.nom_commercial
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
