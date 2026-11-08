using Pharma.Domain.Catalogue;

namespace Pharma.Application.Stock;

public sealed record IndicateursStock(int ReferencesEnStock, decimal ValeurAchat, int LotsDateAVerifier);

/// <summary>
/// Une ligne du tableau de stock. <see cref="Statut"/> vient de la vue v_etat_stock :
/// ok, rupture, critique, dormant ou surstock.
/// </summary>
public sealed record LigneStock(
    long ProduitId, string NomCommercial, string? Dci, string? CodeBarre, ClasseReglementaire Classe,
    int Stock, int? CouvertureJours, DateOnly? PremierePeremption, int NombreLots,
    IReadOnlyList<string> Grossistes, IReadOnlyList<string> NumerosLots,
    string Statut, bool PeremptionProche);

public sealed record EtatStock(IndicateursStock Indicateurs, IReadOnlyList<LigneStock> Lignes);

public interface IStockLecture
{
    Task<IndicateursStock> IndicateursAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LigneStock>> LignesAsync(CancellationToken ct = default);
}

/// <summary>Écran Stock : indicateurs et liste des produits actifs.</summary>
public sealed class ConsulterStock(IStockLecture stock)
{
    public async Task<EtatStock> ExecuterAsync(CancellationToken ct = default) =>
        new(await stock.IndicateursAsync(ct), await stock.LignesAsync(ct));
}
