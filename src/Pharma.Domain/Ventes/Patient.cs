using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

public class Patient
{
    private decimal _solde;

    public long Id { get; init; }
    public string Nom { get; init; } = "";
    public string? Prenom { get; init; }
    public string? Telephone { get; init; }
    public string? NumAssure { get; init; }
    public long? OrganismeId { get; init; }
    public decimal PlafondCredit { get; init; }

    /// <summary>Ce que le patient doit à l'officine. Ne change que par <see cref="AccorderCredit"/>.</summary>
    public decimal SoldeCredit { get => _solde; init => _solde = value; }

    public void AccorderCredit(decimal montant, bool peutDepasserPlafond)
    {
        if (montant <= 0) throw new ArgumentOutOfRangeException(nameof(montant));
        if (!peutDepasserPlafond && _solde + montant > PlafondCredit)
            throw new RegleMetierException(
                $"Plafond de crédit dépassé : {_solde + montant:N2} DA pour un plafond de {PlafondCredit:N2} DA.");
        _solde += montant;
    }
}
