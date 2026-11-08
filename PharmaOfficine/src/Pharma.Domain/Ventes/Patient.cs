using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

public class Patient
{
    public long Id { get; init; }
    public string Nom { get; init; } = "";
    public string? Prenom { get; init; }
    public string? Telephone { get; init; }
    public string? NumAssure { get; init; }
    public long? OrganismeId { get; init; }
    public decimal PlafondCredit { get; init; }
    public decimal SoldeCredit { get; private set; }

    public void AccorderCredit(decimal montant, bool peutDepasserPlafond)
    {
        if (montant <= 0) throw new ArgumentOutOfRangeException(nameof(montant));
        if (!peutDepasserPlafond && SoldeCredit + montant > PlafondCredit)
            throw new RegleMetierException(
                $"Plafond de crédit dépassé : {SoldeCredit + montant:N2} DA pour un plafond de {PlafondCredit:N2} DA.");
        SoldeCredit += montant;
    }
}
