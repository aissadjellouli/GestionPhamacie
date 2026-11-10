using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Remboursement d'un crédit client au comptoir : au plus ce que le patient doit.</summary>
public static class RegleReglementCredit
{
    public static void Verifier(decimal montant, decimal soldeCredit, string? mode)
    {
        if (mode is not ("especes" or "carte" or "cheque")) throw new RegleMetierException("Mode de règlement : espèces, carte ou chèque.");
        if (montant <= 0) throw new RegleMetierException("Le montant réglé doit être supérieur à 0.");
        if (soldeCredit <= 0) throw new RegleMetierException("Ce patient n’a pas de crédit à rembourser.");
        if (montant > soldeCredit) throw new RegleMetierException($"Le patient ne doit que {soldeCredit:N2} DA.");
    }
}
