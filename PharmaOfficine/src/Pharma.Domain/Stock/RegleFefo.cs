namespace Pharma.Domain.Stock;

/// <summary>Lot comparé au comptoir. Niveau : perime, rouge, orange, risque_ecoulement ou ok (seuils de l'Administration).</summary>
public sealed record LotComptoir(long Id, string NumeroLot, DateOnly DatePeremption, string Niveau);

/// <summary>
/// Premier périmé, premier sorti : la boîte scannée est servie telle quelle, mais si un autre lot du même produit
/// périme avant elle et que sa péremption est proche, le caissier est prévenu pour servir ce lot d'abord.
/// </summary>
public static class RegleFefo
{
    /// <summary>Niveaux de péremption « proche » (seuils rouge et orange réglés par produit, grossiste ou en général).</summary>
    public static bool EstProche(string niveau) => niveau is "rouge" or "orange";

    public static string? Avertir(LotComptoir scanne, LotComptoir? premier)
    {
        if (premier is null || premier.Id == scanne.Id || premier.DatePeremption >= scanne.DatePeremption || !EstProche(premier.Niveau))
            return null;
        return $"Boîte plus récente : le lot {premier.NumeroLot} périme le {premier.DatePeremption:dd/MM/yyyy} (péremption proche), " +
               $"celle-ci le {scanne.DatePeremption:dd/MM/yyyy}. Servez d’abord une boîte du lot {premier.NumeroLot}.";
    }
}
