using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Ce que devient la boîte reprise au patient : choix du pharmacien.</summary>
public enum DestinationRetour { Stock, Destruction }

/// <summary>
/// Retour de médicaments par le patient après la vente, à l'appréciation du pharmacien : retour partiel possible,
/// boîtes remises en stock ou à détruire, part payée par le patient rendue au prorata.
/// Vente en tiers payant (décision du titulaire du 2026-11-21, option A, script 155) : avant tout dépôt (vente sur aucun bordereau),
/// la part de l'organisme des boîtes rendues est retirée de la créance, au prorata de la part retenue (part Chifa si la vente a été
/// régularisée après rapprochement de son volet, sinon part de la caisse ; majoration à part). Après dépôt : refusé. Une ligne sans
/// part organisme (non remboursée) se rend toujours.
/// </summary>
public static class RegleRetour
{
    /// <summary>Message exact du titulaire (2026-11-21) : vente sur un bordereau déposé, retour ou annulation impossibles dans le logiciel.</summary>
    public const string MessageDeposee = "Vente déposée à la CNAS : retour impossible dans le logiciel tant que la procédure de la CNAS n’est pas connue";

    public static void VerifierVente(string statut)
    {
        if (statut != "validee") throw new RegleMetierException("Seule une vente encaissée (non annulée) peut faire l’objet d’un retour.");
    }

    /// <summary>
    /// Boîte rendue qui a une part organisme : vente sur aucun bordereau (même condition que l'annulation). Déposée : refus (message du
    /// titulaire) ; sur un bordereau en préparation : la retirer d'abord du bordereau.
    /// </summary>
    public static void VerifierPartOrganisme(bool deposee, bool dansUnBordereau)
    {
        if (deposee) throw new RegleMetierException(MessageDeposee);
        if (dansUnBordereau)
            throw new RegleMetierException("Cette vente figure sur un bordereau Chifa en préparation : retirez-la d’abord du bordereau (Finances › Bordereaux Chifa), puis faites le retour.");
    }

    /// <summary>
    /// Rapport entre ce qui reste de la part retenue de l'organisme (hors majoration) et la part de la caisse des boîtes remboursées
    /// encore chez le patient (script 157) : 1 sans régularisation, part Chifa / part caisse après régularisation, quel que soit l'ordre
    /// du retour et du rattachement du volet ; 0 s'il ne reste rien (part retenue épuisée, ou plus aucune boîte remboursée).
    /// </summary>
    public static decimal RatioRetenu(decimal partRetenue, decimal sommeLignesOrganisme) =>
        sommeLignesOrganisme <= 0 || partRetenue <= 0 ? 0 : partRetenue / sommeLignesOrganisme;

    /// <summary>Part de la caisse (organisme) des boîtes d'une ligne encore chez le patient, non arrondie.</summary>
    public static decimal PartCaisseRestante(decimal montantOrganismeLigne, int quantiteVendue, int dejaRendue) =>
        quantiteVendue <= 0 || montantOrganismeLigne <= 0 ? 0
            : montantOrganismeLigne * Math.Max(quantiteVendue - dejaRendue, 0) / quantiteVendue;

    /// <summary>Part de l'organisme des boîtes rendues d'une ligne : prorata des boîtes, ramené à la part retenue (arrondi au centime).</summary>
    public static decimal MontantOrganisme(decimal montantOrganismeLigne, int quantiteVendue, int quantiteRendue, decimal ratioRetenu) =>
        quantiteVendue <= 0 || montantOrganismeLigne <= 0 || ratioRetenu <= 0 ? 0
            : Math.Round(montantOrganismeLigne * quantiteRendue * ratioRetenu / quantiteVendue, 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Part organisme retirée par ce retour : au plus ce qui reste de la part retenue ; quand toutes les boîtes remboursées sont
    /// désormais rendues, le solde exact (les arrondis des retours successifs ne laissent ni ne retirent un centime de trop).
    /// </summary>
    public static decimal TotalOrganisme(decimal calcule, decimal partRetenue, decimal dejaRetiree, bool toutRembourseRendu)
    {
        var reste = Math.Max(partRetenue - dejaRetiree, 0);
        return toutRembourseRendu ? reste : Math.Min(calcule, reste);
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
