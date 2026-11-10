namespace Pharma.Domain.Achats;

/// <summary>Livraison d'un grossiste : chaque lot reçu y est rattaché.</summary>
public class Reception
{
    public long Id { get; init; }
    public long FournisseurId { get; init; }
    public Fournisseur? Fournisseur { get; init; }
    public string? NumeroBl { get; init; }
    public string? NumeroFacture { get; init; }
    public DateTime DateReception { get; init; }
}
