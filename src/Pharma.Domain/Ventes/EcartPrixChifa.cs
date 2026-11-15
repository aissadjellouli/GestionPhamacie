using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Prix d'une ligne reprise d'un volet Chifa quand le prix Chifa (QR du volet) diffère du nôtre (prix du lot servi), décisions du titulaire
/// du 2026-11-15 :
/// <list type="bullet">
/// <item>écart par boîte (valeur absolue) sous le seuil (Administration › Caisse, 5 DA par défaut) : aligné d'office sur le prix Chifa ;</item>
/// <item>au-delà, vendeur avec le droit prix.modifier : il choisit « s'aligner sur Chifa » ou « garder notre prix » (l'écart est alors
/// à la charge du patient) ;</item>
/// <item>au-delà, vendeur sans le droit : le plus élevé des deux prix d'office (le code comptoir d'un ayant droit permet de choisir).</item>
/// </list>
/// Le catalogue n'est jamais modifié. La part de l'organisme se calcule toujours sur le prix Chifa.
/// </summary>
public static class EcartPrixChifa
{
    public const decimal SeuilParDefaut = 5m;

    public const string AligneAuto = "aligne_auto";
    public const string Aligne = "aligne";
    public const string Garde = "garde";
    public const string PlusEleve = "plus_eleve";

    public static decimal Ecart(decimal prixChifa, decimal notrePrix) => Math.Abs(prixChifa - notrePrix);

    /// <summary>Écart d'au moins le seuil : le prix se choisit (droit prix.modifier), sinon le plus élevé d'office.</summary>
    public static bool AChoisir(decimal prixChifa, decimal notrePrix, decimal seuil) =>
        prixChifa != notrePrix && Ecart(prixChifa, notrePrix) >= seuil;

    /// <summary>
    /// Prix appliqué et choix retenu (null : prix identiques). <paramref name="choix"/> : « aligne » ou « garde » demandé par le poste
    /// (null : aucun). Sans le droit, un choix demandé est refusé (<paramref name="exiger"/>) ou ignoré (aperçu) ; avec le droit, l'absence
    /// de choix est refusée à l'encaissement et vaut provisoirement le plus élevé dans l'aperçu.
    /// </summary>
    public static (decimal Prix, string? Choix) Appliquer(decimal prixChifa, decimal notrePrix, decimal seuil, string? choix, bool aLeDroit,
        bool exiger, string produit)
    {
        if (prixChifa == notrePrix) return (notrePrix, null);
        if (!AChoisir(prixChifa, notrePrix, seuil)) return (prixChifa, AligneAuto);
        var plusEleve = (Math.Max(prixChifa, notrePrix), PlusEleve);
        switch (choix)
        {
            case null or "":
                if (aLeDroit && exiger)
                    throw new RegleMetierException(
                        $"{produit} : prix Chifa {prixChifa:N2} DA, notre prix {notrePrix:N2} DA. Choisissez « S’aligner sur Chifa » ou « Garder notre prix ».",
                        "prix_chifa");
                return plusEleve;
            case Aligne or Garde:
                if (!aLeDroit)
                {
                    if (exiger) throw new PrixChifaNonAutoriseException(produit);
                    return plusEleve;
                }
                return choix == Aligne ? (prixChifa, Aligne) : (notrePrix, Garde);
            case PlusEleve:
                return plusEleve;
            default:
                throw new RegleMetierException($"{produit} : choix de prix Chifa inconnu ({choix}).", "prix_chifa");
        }
    }

    /// <summary>Écart payé par le patient sur une ligne : notre prix gardé (ou appliqué d'office) au-dessus du prix Chifa.</summary>
    public static decimal EcartPatient(decimal prixApplique, decimal? prixChifa, int quantite) =>
        prixChifa is { } c && prixApplique > c ? (prixApplique - c) * quantite : 0;
}

/// <summary>Choix du prix d'une ligne de volet Chifa demandé sans le droit prix.modifier : à faire valider par un ayant droit.</summary>
public sealed class PrixChifaNonAutoriseException(string produit)
    : Exception($"{produit} : choisir entre le prix Chifa et notre prix demande le droit « modifier les prix ». Sans lui, le plus élevé des deux s’applique ; faites valider par un employé qui l’a.");
