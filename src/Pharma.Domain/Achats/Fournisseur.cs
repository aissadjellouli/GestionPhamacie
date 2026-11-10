namespace Pharma.Domain.Achats;

/// <summary>Grossiste. Ses seuils de péremption, s'ils existent, priment sur le réglage général.</summary>
public class Fournisseur
{
    public long Id { get; init; }
    public string RaisonSociale { get; init; } = "";
    public string? Telephone { get; init; }
    public decimal Solde { get; init; }
    public int? JoursOrange { get; init; }
    public int? JoursRouge { get; init; }
}
