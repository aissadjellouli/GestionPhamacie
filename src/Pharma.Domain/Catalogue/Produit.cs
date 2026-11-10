namespace Pharma.Domain.Catalogue;

public class Produit
{
    public long Id { get; init; }
    public string? NumEnregistrement { get; init; }
    public string? CodeBarre { get; init; }
    public string NomCommercial { get; init; } = "";
    public long? DciId { get; init; }
    public Dci? Dci { get; init; }
    public string? Dosage { get; init; }
    public string? Forme { get; init; }
    public decimal PrixPublic { get; init; }
    public decimal? TarifReference { get; init; }
    public bool Remboursable { get; init; }
    public ClasseReglementaire Classe { get; init; } = ClasseReglementaire.Libre;
    /// <summary>Seuil d'alerte saisi (boîtes) ; null : seuil automatique.</summary>
    public int? StockMinimum { get; init; }
    public bool Actif { get; init; } = true;

    /// <summary>Psychotropes et stupéfiants : inscription au registre obligatoire.</summary>
    public bool EstSousRegistre =>
        Classe is ClasseReglementaire.Psychotrope or ClasseReglementaire.Stupefiant;
}
