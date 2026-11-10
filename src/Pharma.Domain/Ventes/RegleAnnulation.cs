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

    public static void VerifierAnnulable(string statut, bool dansUnBordereau, string? factureNumero = null)
    {
        if (statut == "annulee") throw new RegleMetierException("Cette vente est déjà annulée.");
        if (statut != "validee") throw new RegleMetierException("Seule une vente encaissée peut être annulée.");
        if (dansUnBordereau)
            throw new RegleMetierException("Cette vente figure déjà sur un bordereau envoyé à l’organisme : l’annulation doit passer par la régularisation Chifa.");
        // Sinon la facture continuerait de facturer des produits remboursés
        if (factureNumero is not null)
            throw new RegleMetierException($"Cette vente figure sur la facture {factureNumero} : retirez-la d’abord de la facture (Factures : rejet puis correction), ou faites un retour client.");
    }

    /// <summary>
    /// Ce que l'annulation rend, déduction faite des retours déjà rendus (argent ou dette diminuée).
    /// Vente à crédit : la dette diminue de ce qui reste dû ; ce que le patient a déjà remboursé lui est rendu (espèces).
    /// </summary>
    public static (decimal ReductionDette, decimal ARendre) Remboursement(string? mode, decimal partPatient, decimal dejaRendu, decimal soldeCredit)
    {
        var reste = Math.Max(partPatient - dejaRendu, 0);
        if (mode != "credit") return (0, reste);
        var reduction = Math.Min(Math.Max(soldeCredit, 0), reste);
        return (reduction, reste - reduction);
    }
}
