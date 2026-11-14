using Npgsql;
using Pharma.Application.Catalogue;
using Pharma.Domain.Achats;

namespace Pharma.Infrastructure.Lecture;

/// <summary>
/// Prix servi et équivalents au comptoir : stock vendable (lots ni périmés ni bloqués, même calcul que v_etat_stock), prix d'une boîte
/// du lot qui serait servi (premier à périmer : PPA du lot, sinon prix du catalogue) plus le SHP du produit (RegleShp).
/// </summary>
public sealed class EquivalentsComptoirSql(NpgsqlDataSource db) : IEquivalentsComptoir
{
    internal const string Selection = """
        SELECT p.id, p.nom_commercial, p.dci_id, p.dosage, p.forme, COALESCE(e.stock, 0)::int, COALESCE(f.ppa, p.prix_public),
               p.classe, p.liste_psychotrope, lab.nom
        FROM produit p
        LEFT JOIN laboratoire lab ON lab.id = p.laboratoire_id
        LEFT JOIN LATERAL (SELECT sum(l.quantite_disponible) AS stock FROM lot l
                           WHERE l.produit_id = p.id AND l.date_peremption > current_date AND NOT l.bloque) e ON true
        LEFT JOIN LATERAL (SELECT l.ppa FROM lot l
                           WHERE l.produit_id = p.id AND l.date_peremption > current_date AND NOT l.bloque AND l.quantite_disponible > 0
                           ORDER BY l.date_peremption, l.id LIMIT 1) f ON true
        """;

    internal static ProduitPrixServi Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5),
        r.GetDecimal(6) + RegleShp.ParBoite(r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9));

    private async Task<IReadOnlyList<ProduitPrixServi>> LireAsync(string condition, long[] valeurs, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Selection + " WHERE " + condition);
        cmd.Parameters.AddWithValue(valeurs);
        var liste = new List<ProduitPrixServi>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(Lire(r));
        return liste;
    }

    public Task<IReadOnlyList<ProduitPrixServi>> ProduitsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
        LireAsync("p.id = ANY($1)", ids.ToArray(), ct);

    public Task<IReadOnlyList<ProduitPrixServi>> EnStockAsync(IReadOnlyCollection<long> dciIds, CancellationToken ct = default) =>
        LireAsync("p.dci_id = ANY($1) AND p.actif AND COALESCE(e.stock, 0) > 0", dciIds.ToArray(), ct);
}
