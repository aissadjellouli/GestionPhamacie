using Pharma.Application.Commun;
using Pharma.Application.Mobile;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Caisse;

public sealed record LotCaisse(long LotId, string NumeroLot, DateOnly DatePeremption, int Quantite, string Fournisseur);

/// <summary>
/// Boîte identifiée au comptoir (étiquette interne ou Data Matrix) : son lot est celui servi.
/// LotConseille : lot qui périme avant elle avec une péremption proche, à servir d'abord.
/// </summary>
public sealed record BoiteCaisse(long ProduitId, string Produit, decimal PrixPublic, int StockProduit,
    LotCaisse Lot, LotCaisse? LotConseille, string? Avertissement);

/// <summary>
/// Scan en caisse d'un code qui désigne un lot (étiquette interne de boîte ou de lot, Data Matrix).
/// Renvoie null pour un code-barres du fabricant : la caisse sert alors le lot FEFO comme avant.
/// </summary>
public sealed class ControlerBoiteCaisse(IMobile codes, IPermissionService droits, IHorloge horloge)
{
    public async Task<BoiteCaisse?> ExecuterAsync(string? code, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "vendre", ct);
        var brut = code?.Trim() ?? "";
        if (brut.Length == 0) return null;

        CodeResolu? resolu = Gs1DataMatrix.TryParse(brut, out var dm) && dm is not null
            ? await codes.ResoudreDataMatrixAsync(dm.Gtin, dm.Lot is null ? null : RegleLot.Normaliser(dm.Lot), dm.NumeroSerie, ct)
            : await codes.ResoudreCodeAsync(brut, ct);
        if (resolu is null || resolu.Type == "produit" || resolu.LotId is not { } lotId) return null;

        if (resolu.StatutBoite is { } s && s != "en_stock")
            throw new RegleMetierException("Cette boîte n’est plus en stock (déjà vendue, retournée ou détruite) : scannez une autre boîte.");

        var fiche = await codes.FicheAsync(resolu.ProduitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        var aujourdhui = horloge.Aujourdhui;
        var lot = fiche.Lots.FirstOrDefault(l => l.Id == lotId)
            ?? throw new RegleMetierException($"{fiche.Nom} : le lot de cette boîte est épuisé dans le logiciel. Vérifiez le stock.");
        if (lot.Bloque)
            throw new RegleMetierException($"{fiche.Nom}, lot {lot.NumeroLot} : lot bloqué (rappel), vente interdite. Retirez la boîte.");
        if (lot.DatePeremption <= aujourdhui)
            throw new RegleMetierException($"{fiche.Nom}, lot {lot.NumeroLot} : périmé depuis le {lot.DatePeremption:dd/MM/yyyy}, vente interdite.");

        var vendables = fiche.Lots.Where(l => !l.Bloque && l.DatePeremption > aujourdhui && l.Quantite > 0).ToList();
        var premier = vendables.OrderBy(l => l.DatePeremption).ThenBy(l => l.Id).FirstOrDefault();
        var avertissement = RegleFefo.Avertir(
            new LotComptoir(lot.Id, lot.NumeroLot, lot.DatePeremption, lot.Niveau),
            premier is null ? null : new LotComptoir(premier.Id, premier.NumeroLot, premier.DatePeremption, premier.Niveau));

        static LotCaisse VersCaisse(LotFiche l) => new(l.Id, l.NumeroLot, l.DatePeremption, l.Quantite, l.Fournisseur);
        return new BoiteCaisse(fiche.ProduitId, fiche.Nom, fiche.PrixPublic, vendables.Sum(l => l.Quantite),
            VersCaisse(lot), avertissement is null ? null : VersCaisse(premier!), avertissement);
    }
}
