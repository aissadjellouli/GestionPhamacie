namespace Pharma.Domain.Commun;

/// <summary>
/// Arrondis monétaires : au centime, la moitié vers le haut (0,005 → 0,01 ; 2,345 → 2,35), comme sur les factures et à la main.
/// L'arrondi par défaut de .NET (« bancaire », 2,345 → 2,34) n'est jamais utilisé pour un montant.
/// </summary>
public static class Arrondi
{
    /// <summary>Montant arrondi au centime, la moitié s'éloignant de zéro (-2,345 → -2,35).</summary>
    public static decimal Centime(decimal montant) => Math.Round(montant, 2, MidpointRounding.AwayFromZero);

    /// <summary>Vrai si le montant compte au plus deux décimales.</summary>
    public static bool AuCentime(decimal montant) => montant == Centime(montant);
}
