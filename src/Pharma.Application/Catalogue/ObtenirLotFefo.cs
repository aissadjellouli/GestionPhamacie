using Pharma.Application.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Catalogue;

/// <summary>StockProduit : boîtes vendables du produit, tous lots (ni périmées, ni bloquées).</summary>
public sealed record LotAServir(long LotId, string NumeroLot, DateOnly DatePeremption, int Quantite, string Fournisseur, int StockProduit = 0);

/// <summary>Lot encore en stock mais écarté de la vente (périmé ou bloqué).</summary>
public sealed record LotEcarte(string NumeroLot, DateOnly DatePeremption, RaisonNonVendable Raison, string? MotifBlocage);

/// <summary>Lot servi (null si aucun n'est vendable) et lots écartés, pour l'expliquer au comptoir.</summary>
public sealed record ResultatLotFefo(LotAServir? Lot, IReadOnlyList<LotEcarte> Ecartes);

public interface ILotsLecture
{
    /// <summary>Lots du produit avec une quantité disponible, grossiste renseigné.</summary>
    Task<IReadOnlyList<Lot>> LotsEnStockAsync(long produitId, CancellationToken ct = default);
}

/// <summary>Lot à servir en caisse : règle FEFO du domaine (ni périmé, ni bloqué, ni épuisé).</summary>
public sealed class ObtenirLotFefo(ILotsLecture lots, IHorloge horloge)
{
    public async Task<ResultatLotFefo> ExecuterAsync(long produitId, CancellationToken ct = default)
    {
        var aujourdhui = horloge.Aujourdhui;
        var enStock = await lots.LotsEnStockAsync(produitId, ct);
        var lot = Lot.Fefo(enStock, aujourdhui);

        var ecartes = enStock
            .Select(l => (Lot: l, Raison: l.RaisonNonVendable(aujourdhui)))
            .Where(x => x.Raison is RaisonNonVendable.Perime or RaisonNonVendable.Bloque)
            .OrderBy(x => x.Lot.DatePeremption)
            .Select(x => new LotEcarte(x.Lot.NumeroLot, x.Lot.DatePeremption, x.Raison!.Value, x.Lot.MotifBlocage))
            .ToList();

        var servi = lot is null
            ? null
            : new LotAServir(lot.Id, lot.NumeroLot, lot.DatePeremption, lot.QuantiteDisponible,
                lot.Fournisseur?.RaisonSociale ?? "", enStock.Where(l => l.EstVendable(aujourdhui)).Sum(l => l.QuantiteDisponible));
        return new ResultatLotFefo(servi, ecartes);
    }
}
