using Npgsql;
using Pharma.Application.Tracabilite;

namespace Pharma.Infrastructure.Tracabilite;

/// <summary>
/// Numéros de lot d'un produit proposés au rappel, épuisés compris (le rappel cherche le lot par produit et numéro, sans condition de
/// quantité) : un numéro par ligne (lots de plusieurs livraisons réunis), péremption la plus proche, boîtes disponibles additionnées,
/// bloqué si tous ses lots le sont. Saisie toujours en paramètre lié, comparée sans accents (sans_accent) ni majuscules (ILIKE),
/// \, % et _ pris à la lettre ; lecture par l'index ix_lot_produit_numero (produit, numéro ; script 150).
/// </summary>
public sealed class LotsRappelablesSql(NpgsqlDataSource db) : ILotsRappelables
{
    public async Task<IReadOnlyList<LotRappelable>> LotsAsync(long produitId, string texte, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.numero_lot, min(l.date_peremption), COALESCE(sum(l.quantite_disponible), 0)::int, bool_and(l.bloque)
            FROM lot l
            WHERE l.produit_id = $1 AND btrim(l.numero_lot) <> ''
              AND sans_accent(l.numero_lot) ILIKE '%' || sans_accent($2) || '%' ESCAPE '\'
            GROUP BY l.numero_lot
            ORDER BY (COALESCE(sum(l.quantite_disponible), 0) > 0) DESC, min(l.date_peremption) DESC, l.numero_lot
            LIMIT $3
            """);
        cmd.Parameters.AddWithValue(produitId);
        cmd.Parameters.AddWithValue(Referentiels.MotifRecherche.Echapper(texte));   // \, % et _ tapés pris à la lettre
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<LotRappelable>();
        while (await r.ReadAsync(ct))
            liste.Add(new LotRappelable(r.GetString(0), r.GetFieldValue<DateOnly>(1), r.GetInt32(2), r.GetBoolean(3)));
        return liste;
    }
}
