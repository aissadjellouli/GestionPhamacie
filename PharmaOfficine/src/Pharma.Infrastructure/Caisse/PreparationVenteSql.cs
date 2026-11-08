using Npgsql;
using Pharma.Application.Caisse;
using Pharma.Domain.Achats;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Produits et lots du panier, relus en base au moment de l'encaissement (prix et stock à jour).</summary>
public sealed class PreparationVenteSql(NpgsqlDataSource db) : IPreparationVente
{
    public async Task<IReadOnlyDictionary<long, Produit>> ProduitsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom_commercial, prix_public, tarif_reference, remboursable, classe, actif, code_barre
            FROM produit WHERE id = ANY($1)
            """);
        cmd.Parameters.AddWithValue(ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var produits = new Dictionary<long, Produit>();
        while (await r.ReadAsync(ct))
            produits[r.GetInt64(0)] = new Produit
            {
                Id = r.GetInt64(0), NomCommercial = r.GetString(1), PrixPublic = r.GetDecimal(2),
                TarifReference = r.IsDBNull(3) ? null : r.GetDecimal(3), Remboursable = r.GetBoolean(4),
                Classe = CodesMetier.ClasseDepuisCode(r.GetString(5)), Actif = r.GetBoolean(6),
                CodeBarre = r.IsDBNull(7) ? null : r.GetString(7)
            };
        return produits;
    }

    public async Task<IReadOnlyDictionary<long, Lot>> LotsAsync(IEnumerable<long> ids, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT l.id, l.produit_id, l.reception_id, l.fournisseur_id, l.numero_lot, l.date_peremption,
                   l.quantite_disponible, l.prix_achat_ht, l.bloque, l.motif_blocage, l.date_a_verifier, f.raison_sociale
            FROM lot l JOIN fournisseur f ON f.id = l.fournisseur_id
            WHERE l.id = ANY($1)
            """);
        cmd.Parameters.AddWithValue(ids.ToArray());
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lots = new Dictionary<long, Lot>();
        while (await r.ReadAsync(ct))
            lots[r.GetInt64(0)] = new Lot
            {
                Id = r.GetInt64(0), ProduitId = r.GetInt64(1), ReceptionId = r.GetInt64(2), FournisseurId = r.GetInt64(3),
                NumeroLot = r.GetString(4), DatePeremption = r.GetFieldValue<DateOnly>(5),
                QuantiteDisponible = r.GetInt32(6), PrixAchatHt = r.GetDecimal(7), Bloque = r.GetBoolean(8),
                MotifBlocage = r.IsDBNull(9) ? null : r.GetString(9), DateAVerifier = r.GetBoolean(10),
                Fournisseur = new Fournisseur { Id = r.GetInt64(3), RaisonSociale = r.GetString(11) }
            };
        return lots;
    }
}
