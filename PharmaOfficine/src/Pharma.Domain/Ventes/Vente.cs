namespace Pharma.Domain.Ventes;

public enum TypeVente
{
    Libre,
    TiersPayant
}

public enum StatutVente
{
    EnCours,
    Validee,
    Annulee
}

public class Vente
{
    private readonly List<LigneVente> _lignes = [];

    public long Id { get; init; }
    public string Numero { get; init; } = "";
    public DateTime DateVente { get; init; }
    public long UtilisateurId { get; init; }
    public long? PatientId { get; init; }
    public long? OrdonnanceId { get; init; }
    public TypeVente Type { get; init; } = TypeVente.Libre;
    public long? OrganismeId { get; init; }
    public decimal MontantTotal { get; private set; }
    public decimal PartOrganisme { get; private set; }
    public decimal PartPatient { get; private set; }
    public StatutVente Statut { get; private set; } = StatutVente.EnCours;
    public string? NumFactureChifa { get; init; }
    public IReadOnlyList<LigneVente> Lignes => _lignes;

    public void AjouterLigne(LigneVente ligne)
    {
        _lignes.Add(ligne);
        RecalculerTotaux();
    }

    public void RecalculerTotaux()
    {
        MontantTotal = _lignes.Sum(l => l.PrixUnitaire * l.Quantite);
        PartOrganisme = _lignes.Sum(l => l.MontantOrganisme);
        PartPatient = MontantTotal - PartOrganisme;
    }
}
