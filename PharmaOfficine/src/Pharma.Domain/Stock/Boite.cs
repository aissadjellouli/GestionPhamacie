namespace Pharma.Domain.Stock;

public enum StatutBoite
{
    EnStock,
    Vendue,
    RetourneeFournisseur,
    Detruite,
    RetourClient
}

/// <summary>Une boîte identifiée : Data Matrix (GTIN + série) ou étiquette interne.</summary>
public class Boite
{
    public long Id { get; init; }
    public long LotId { get; init; }
    public Lot? Lot { get; init; }
    public string? Gtin { get; init; }
    public string? NumeroSerie { get; init; }
    public string? CodeEtiquette { get; init; }
    public StatutBoite Statut { get; init; } = StatutBoite.EnStock;
    public long? LigneVenteId { get; init; }
}
