using Pharma.Domain.Commun;

namespace Pharma.Application.Tracabilite;

/// <summary>
/// Un numéro de lot d'un produit, proposé au rappel : tous les lots de ce numéro réunis (plusieurs livraisons ou grossistes),
/// épuisés compris. DatePeremption : la plus proche ; Quantite : boîtes disponibles de tous ces lots ; Bloque : tous bloqués.
/// </summary>
public sealed record LotRappelable(string NumeroLot, DateOnly DatePeremption, int Quantite, bool Bloque)
{
    /// <summary>Plus aucune boîte disponible : le lot reste rappelable (boîtes vendues).</summary>
    public bool Epuise => Quantite <= 0;
}

/// <summary>Numéros de lot d'un produit, en stock ou épuisés (lecture seule).</summary>
public interface ILotsRappelables
{
    /// <summary>
    /// Numéros distincts des lots du produit dont le numéro contient <paramref name="texte"/> (sans accents ni majuscules ; vide : tous),
    /// lots en stock d'abord puis péremption la plus lointaine, <paramref name="limite"/> au plus.
    /// </summary>
    Task<IReadOnlyList<LotRappelable>> LotsAsync(long produitId, string texte, int limite, CancellationToken ct = default);
}

/// <summary>
/// GET /api/tracabilite/produits/{id}/lots?q= (décision du titulaire, 2026-11-18) : numéros de lot proposés au rappel, épuisés compris
/// (le rappel cherche le lot sans condition de quantité), 10 au plus. Droit : celui du rappel (stock.declarer_perime), exigé par la route.
/// </summary>
public sealed class ProposerLotsRappel(ILotsRappelables lots)
{
    /// <summary>Propositions au plus (charte : 10).</summary>
    public const int Limite = 10;

    public async Task<IReadOnlyList<LotRappelable>> ExecuterAsync(long produitId, string? texte, CancellationToken ct = default)
    {
        if (produitId <= 0) throw new RegleMetierException("Choisissez le produit concerné par le rappel.", "produit_rappel");
        var liste = await lots.LotsAsync(produitId, texte?.Trim() ?? "", Limite, ct);
        return liste.Take(Limite).ToList();
    }
}
