using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Ce que devient la boîte reprise au patient : choix du pharmacien.</summary>
public enum DestinationRetour { Stock, Destruction }

/// <summary>
/// Retour de médicaments par le patient après la vente, à l'appréciation du pharmacien : retour partiel possible,
/// boîtes remises en stock ou à détruire, part payée par le patient rendue au prorata.
/// </summary>
public static class RegleRetour
{
    public static void VerifierVente(string statut, bool dansUnBordereau, decimal partOrganisme)
    {
        if (statut != "validee") throw new RegleMetierException("Seule une vente encaissée (non annulée) peut faire l’objet d’un retour.");
        if (dansUnBordereau || partOrganisme > 0)
            throw new RegleMetierException("Vente en tiers payant : un retour partiel fausserait la part de l’organisme. Annulez la vente complète, puis refaites-la sans le produit rendu.");
    }

    /// <summary>Quantité rendue : au moins 1, au plus ce qui a été vendu moins ce qui a déjà été rendu.</summary>
    public static void VerifierQuantite(string produit, int quantite, int vendue, int dejaRendue)
    {
        if (quantite <= 0) throw new RegleMetierException($"{produit} : quantité rendue invalide.");
        if (quantite > vendue - dejaRendue)
            throw new RegleMetierException($"{produit} : {vendue - dejaRendue} boîte(s) au plus peuvent encore être rendues ({vendue} vendue(s), {dejaRendue} déjà rendue(s)).");
    }

    /// <summary>Part payée par le patient pour les boîtes rendues (au prorata, arrondie au centime).</summary>
    public static decimal Montant(decimal montantPatientLigne, int quantiteVendue, int quantiteRendue) =>
        quantiteVendue <= 0 ? 0 : Math.Round(montantPatientLigne * quantiteRendue / quantiteVendue, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Part du patient remise appliquée : les lignes portent le prix avant la remise de la vente ; le patient a payé
    /// partPatient (= somme des lignes - remise). Montant d'une ligne rendue au prorata de ce qui a été payé.
    /// </summary>
    public static decimal MontantPaye(decimal montantPatientLigne, int quantiteVendue, int quantiteRendue, decimal partPatient, decimal brutPatient) =>
        brutPatient <= 0 ? 0
            : Math.Round(Montant(montantPatientLigne, quantiteVendue, quantiteRendue) * Math.Min(partPatient / brutPatient, 1m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Total à rendre pour ce retour : au plus ce qui reste de la part payée. Si tout est désormais rendu, le solde exact
    /// (les arrondis des retours successifs ne font ni perdre ni gagner un centime).
    /// </summary>
    public static decimal TotalARendre(decimal calcule, decimal partPatient, decimal dejaRendu, bool toutRendu)
    {
        var reste = Math.Max(partPatient - dejaRendu, 0);
        return toutRendu ? reste : Math.Min(calcule, reste);
    }

    public static DestinationRetour Destination(string? code) => code switch
    {
        "stock" => DestinationRetour.Stock,
        "destruction" => DestinationRetour.Destruction,
        _ => throw new RegleMetierException("Choisissez pour chaque produit : remettre en stock ou à détruire.")
    };

    public static string Code(this DestinationRetour d) => d == DestinationRetour.Stock ? "stock" : "destruction";
}
