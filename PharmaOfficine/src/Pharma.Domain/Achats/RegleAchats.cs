using Pharma.Domain.Commun;

namespace Pharma.Domain.Achats;

public sealed record LigneCommandeSaisie(long ProduitId, string Nom, int Quantite, decimal PrixAchatHt);

/// <summary>Une ligne de livraison : un lot d'un produit, avec ses boîtes sérialisées éventuelles (Data Matrix).</summary>
public sealed record LigneReceptionSaisie(
    long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite, decimal PrixAchatHt,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null);

public static class RegleAchats
{
    public const int QuantiteMax = 10_000;

    public static void VerifierCommande(IReadOnlyList<LigneCommandeSaisie> lignes)
    {
        if (lignes.Count == 0)
            throw new RegleMetierException("Le bon de commande est vide.");
        foreach (var l in lignes)
        {
            if (l.Quantite is <= 0 or > QuantiteMax)
                throw new RegleMetierException($"{l.Nom} : quantité à commander entre 1 et {QuantiteMax}.");
            if (l.PrixAchatHt < 0)
                throw new RegleMetierException($"{l.Nom} : le prix d’achat ne peut pas être négatif.");
        }
        if (lignes.Select(l => l.ProduitId).Distinct().Count() != lignes.Count)
            throw new RegleMetierException("Un même produit figure deux fois sur le bon de commande.");
    }

    /// <summary>Contrôles d'une livraison avant l'entrée en stock. Un lot déjà périmé est refusé.</summary>
    public static void VerifierReception(string? numeroBl, IReadOnlyList<LigneReceptionSaisie> lignes, DateOnly aujourdhui)
    {
        if (string.IsNullOrWhiteSpace(numeroBl))
            throw new RegleMetierException("Indiquez le numéro du bon de livraison.");
        if (lignes.Count == 0)
            throw new RegleMetierException("La livraison ne contient aucune ligne.");
        foreach (var l in lignes)
        {
            if (string.IsNullOrWhiteSpace(l.NumeroLot) || l.NumeroLot.Trim().Length > 40)
                throw new RegleMetierException($"{l.Nom} : numéro de lot obligatoire (40 caractères au plus).");
            if (l.DatePeremption <= aujourdhui)
                throw new RegleMetierException($"{l.Nom}, lot {l.NumeroLot} : déjà périmé ({l.DatePeremption:dd/MM/yyyy}), réception refusée.");
            if (l.Quantite is <= 0 or > QuantiteMax)
                throw new RegleMetierException($"{l.Nom}, lot {l.NumeroLot} : quantité reçue entre 1 et {QuantiteMax}.");
            if (l.PrixAchatHt < 0)
                throw new RegleMetierException($"{l.Nom} : le prix d’achat ne peut pas être négatif.");
            if (l.NumerosSerie is { Count: > 0 } series)
            {
                if (string.IsNullOrWhiteSpace(l.Gtin))
                    throw new RegleMetierException($"{l.Nom} : les numéros de série exigent le GTIN du Data Matrix.");
                if (series.Count > l.Quantite)
                    throw new RegleMetierException($"{l.Nom}, lot {l.NumeroLot} : plus de boîtes scannées ({series.Count}) que reçues ({l.Quantite}).");
                if (series.Distinct().Count() != series.Count)
                    throw new RegleMetierException($"{l.Nom}, lot {l.NumeroLot} : une même boîte a été scannée deux fois.");
            }
        }
    }

    /// <summary>Montant TTC d'une ligne d'achat, arrondi au centime.</summary>
    public static decimal MontantTtc(int quantite, decimal prixHt, decimal tauxTvaPct) =>
        Math.Round(quantite * prixHt * (1 + tauxTvaPct / 100m), 2, MidpointRounding.AwayFromZero);
}

public enum MotifRetour { PeremptionProche, Perime, Defaut, ErreurLivraison, RappelLot }
public enum StatutRetour { Brouillon, Envoye, AvoirRecu, Refuse }

/// <summary>Retour au grossiste d'origine du lot : brouillon → envoyé (le stock sort) → avoir reçu ou refusé.</summary>
public static class RegleRetour
{
    public static void VerifierCreation(int quantite, int disponible, string lot)
    {
        if (quantite <= 0)
            throw new RegleMetierException("Indiquez le nombre de boîtes à retourner.");
        if (quantite > disponible)
            throw new RegleMetierException($"Lot {lot} : {disponible} boîte(s) en stock, {quantite} à retourner.");
    }

    public static void VerifierTransition(StatutRetour actuel, StatutRetour voulu)
    {
        var permis = (actuel, voulu) switch
        {
            (StatutRetour.Brouillon, StatutRetour.Envoye) => true,
            (StatutRetour.Brouillon, StatutRetour.Refuse) => true,      // abandon avant envoi
            (StatutRetour.Envoye, StatutRetour.AvoirRecu) => true,
            (StatutRetour.Envoye, StatutRetour.Refuse) => true,         // refusé par le grossiste : stock réintégré
            _ => false
        };
        if (!permis)
            throw new RegleMetierException($"Ce retour est « {Libelle(actuel)} » : impossible de le passer à « {Libelle(voulu)} ».");
    }

    public static string Libelle(StatutRetour s) => s switch
    {
        StatutRetour.Brouillon => "brouillon",
        StatutRetour.Envoye => "envoyé",
        StatutRetour.AvoirRecu => "avoir reçu",
        _ => "refusé"
    };
}
