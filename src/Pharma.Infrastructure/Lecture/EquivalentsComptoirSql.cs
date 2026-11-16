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
               p.classe, p.liste_psychotrope, COALESCE(lab.nom, nl.nom), COALESCE(fl.fabrication_locale, false)
        FROM produit p
        LEFT JOIN laboratoire lab ON lab.id = p.laboratoire_id
        -- Fabriqué en Algérie (116) : saisie, sinon pays de la nomenclature
        LEFT JOIN v_produit_fabrication_locale fl ON fl.produit_id = p.id
        -- Laboratoire absent de la fiche (produit.laboratoire_id jamais renseigné) : celui de la nomenclature nationale, par le
        -- n° d'enregistrement (version la plus récente, produit en vigueur d'abord)
        LEFT JOIN LATERAL (SELECT btrim(n.laboratoire) AS nom FROM nomenclature_produit n
                           WHERE lab.id IS NULL AND n.num_enregistrement = p.num_enregistrement AND btrim(n.laboratoire) <> ''
                           ORDER BY n.version_id DESC, n.situation = 'en_vigueur' DESC, n.id LIMIT 1) nl ON true
        LEFT JOIN LATERAL (SELECT sum(l.quantite_disponible) AS stock FROM lot l
                           WHERE l.produit_id = p.id AND l.date_peremption > current_date AND NOT l.bloque) e ON true
        LEFT JOIN LATERAL (SELECT l.ppa FROM lot l
                           WHERE l.produit_id = p.id AND l.date_peremption > current_date AND NOT l.bloque AND l.quantite_disponible > 0
                           ORDER BY l.date_peremption, l.id LIMIT 1) f ON true
        """;

    internal static ProduitPrixServi Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4), r.GetInt32(5),
        r.GetDecimal(6) + RegleShp.ParBoite(r.GetString(7), r.IsDBNull(8) ? null : r.GetString(8)), r.IsDBNull(9) ? null : r.GetString(9),
        r.GetBoolean(10));

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

    public async Task<IReadOnlyList<RemplacantAutorise>> ListeBlancheEnStockAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default)
    {
        if (produitIds.Count == 0) return [];
        var liens = await ListeBlancheAsync(db, produitIds, ct);
        if (liens.Count == 0) return [];
        var remplacants = (await LireAsync("p.id = ANY($1) AND p.actif AND COALESCE(e.stock, 0) > 0",
            liens.Select(l => l.Remplacant).Distinct().ToArray(), ct)).ToDictionary(p => p.Id);
        return liens.Where(l => remplacants.ContainsKey(l.Remplacant)).Select(l => new RemplacantAutorise(l.Produit, remplacants[l.Remplacant])).ToList();
    }

    /// <summary>Liens de la liste blanche (116) de ces produits.</summary>
    internal static async Task<List<(long Produit, long Remplacant)>> ListeBlancheAsync(NpgsqlDataSource db, IReadOnlyCollection<long> produitIds, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT produit_id, remplacant_id FROM substitution_liste_blanche WHERE produit_id = ANY($1)");
        cmd.Parameters.AddWithValue(produitIds.ToArray());
        var liens = new List<(long, long)>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liens.Add((r.GetInt64(0), r.GetInt64(1)));
        return liens;
    }
}
