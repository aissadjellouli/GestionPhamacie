namespace Pharma.Domain.Stock;

/// <summary>Lot comparé au comptoir. Niveau : perime, rouge, orange, risque_ecoulement ou ok (seuils de l'Administration).</summary>
public sealed record LotComptoir(long Id, string NumeroLot, DateOnly DatePeremption, string Niveau);

/// <summary>
/// Premier périmé, premier sorti : la boîte scannée est servie telle quelle, mais si un autre lot du même produit périme
/// avant elle, le caissier est prévenu pour servir ce lot d'abord : alerte rouge si ce lot est proche de sa péremption
/// (seuils rouge ou orange), orange sinon.
/// </summary>
public static class RegleFefo
{
    /// <summary>Niveaux de péremption « proche » (seuils rouge et orange réglés par produit, grossiste ou en général).</summary>
    public static bool EstProche(string niveau) => niveau is "rouge" or "orange";

    public static string? Avertir(LotComptoir scanne, LotComptoir? premier)
    {
        if (premier is null || premier.Id == scanne.Id || premier.DatePeremption >= scanne.DatePeremption)
            return null;
        return $"Boîte plus récente : le lot {premier.NumeroLot} périme le {premier.DatePeremption:dd/MM/yyyy}" +
               (EstProche(premier.Niveau) ? " (péremption proche)" : "") +
               $", celle-ci le {scanne.DatePeremption:dd/MM/yyyy}. Servez d’abord une boîte du lot {premier.NumeroLot}.";
    }

    /// <summary>Couleur de l'alerte : rouge si le lot à servir d'abord est proche de sa péremption, orange sinon.</summary>
    public static string Couleur(LotComptoir premier) => EstProche(premier.Niveau) ? "rouge" : "orange";
}
