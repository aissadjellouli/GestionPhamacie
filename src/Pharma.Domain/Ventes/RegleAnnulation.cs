using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Qui doit autoriser l'annulation : personne de plus (vente récente ou employé habilité), ou un validateur.</summary>
public enum AutorisationAnnulation { Directe, ValidationRequise }

/// <summary>
/// Annulation d'une vente encaissée, à l'appréciation du pharmacien et de son assistant, sans limite de durée.
/// Vente récente (sa caisse est encore ouverte) : le caissier l'annule seul. Vente ancienne (caisse clôturée) :
/// le droit « caisse.annuler_vente » est exigé, de l'employé ou d'un validateur présent (code comptoir).
/// </summary>
public static class RegleAnnulation
{
    public const int MotifMin = 3;

    public static string Motif(string? motif) =>
        string.IsNullOrWhiteSpace(motif) || motif.Trim().Length < MotifMin
            ? throw new RegleMetierException("Indiquez le motif de l’annulation.")
            : motif.Trim().Length > 300 ? throw new RegleMetierException("Motif : 300 caractères au plus.") : motif.Trim();

    public static AutorisationAnnulation Autorisation(bool caisseDeLaVenteOuverte, bool employeHabilite) =>
        caisseDeLaVenteOuverte || employeHabilite ? AutorisationAnnulation.Directe : AutorisationAnnulation.ValidationRequise;

    /// <param name="deposee">Vente portée sur un bordereau déposé (sorti de « préparation ») : refus avec le message du titulaire (2026-11-21).</param>
    public static void VerifierAnnulable(string statut, bool dansUnBordereau, string? factureNumero = null, bool deposee = false)
    {
        if (statut == "annulee") throw new RegleMetierException("Cette vente est déjà annulée.");
        if (statut != "validee") throw new RegleMetierException("Seule une vente encaissée peut être annulée.");
        // Aucune procédure de la CNAS connue pour corriger une vente déjà déposée (pas de « régularisation Chifa » dans le logiciel)
        if (deposee) throw new RegleMetierException(RegleRetour.MessageDeposee);
        if (dansUnBordereau)
            throw new RegleMetierException("Cette vente figure sur un bordereau Chifa en préparation : retirez-la d’abord du bordereau (Finances › Bordereaux Chifa), puis annulez-la.");
        // Sinon la facture continuerait de facturer des produits remboursés
        if (factureNumero is not null)
            throw new RegleMetierException($"Cette vente figure sur la facture {factureNumero} : retirez-la d’abord de la facture (Factures : rejet puis correction), ou faites un retour client.");
    }

    /// <summary>
    /// Ce que l'annulation rend, déduction faite des retours déjà rendus (argent ou dette diminuée).
    /// Vente à crédit : la dette diminue de ce qui reste dû ; ce que le patient a déjà remboursé lui est rendu (espèces).
    /// Vente payée (espèces, carte, chèque) à un patient qui doit de l'argent : si le caissier le choisit (solderDette), le montant
    /// à rendre est d'abord retiré de cette dette ; seul le surplus est rendu.
    /// </summary>
    /// <summary>Remboursement d'une vente payée retiré de la dette du patient : seulement avec son accord.</summary>
    public static RegleMetierException AccordClientManquant() =>
        new("Pour déduire le remboursement de sa dette, le client doit l’accepter : cochez son accord, ou rendez-lui l’argent.", "accord_client");

    /// <summary>
    /// Vente à crédit, annulée (montant = tout ce qui reste de la vente) ou en partie rendue (montant des boîtes rendues).
    /// resteVente : part patient moins ce qui a déjà été rendu ; payeSurVente : remboursements réellement versés par le patient
    /// et imputés à cette vente (les plus anciennes dettes d'abord ; une dette corrigée ou abandonnée n'est pas un paiement).
    /// La dette baisse d'abord, au plus de ce qui reste impayé sur la vente ; seul ce qui a vraiment été payé est rendu.
    /// </summary>
    public static (decimal ReductionDette, decimal ARendre) RemboursementCredit(decimal montant, decimal resteVente, decimal payeSurVente,
        decimal soldeCredit)
    {
        montant = Math.Max(montant, 0);
        var paye = Math.Clamp(payeSurVente, 0, Math.Max(resteVente, 0));
        var nonPaye = Math.Max(resteVente - paye, 0);
        var reduction = Math.Min(Math.Min(Math.Max(soldeCredit, 0), nonPaye), montant);
        return (reduction, Math.Min(montant - reduction, paye));
    }

    public static (decimal ReductionDette, decimal ARendre) Remboursement(string? mode, decimal partPatient, decimal dejaRendu, decimal soldeCredit,
        bool solderDette = false)
    {
        var reste = Math.Max(partPatient - dejaRendu, 0);
        if (mode != "credit" && !solderDette) return (0, reste);
        var reduction = Math.Min(Math.Max(soldeCredit, 0), reste);
        return (reduction, reste - reduction);
    }
}
