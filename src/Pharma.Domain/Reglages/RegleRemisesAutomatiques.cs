using Pharma.Domain.Commun;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Remises automatiques (table remise_automatique), extraites de GererRemisesAutomatiques : un produit OU une catégorie, pourcentage entier de 1 à
/// 100, période du début à la fin incluse, motif de 200 caractères au plus, jamais sur un produit remboursable, jamais deux remises qui se
/// chevauchent sur la même cible. Un modèle central ne porte que des catégories (D6).
/// </summary>
public static class RegleRemisesAutomatiques
{
    public const int MotifMax = 200;

    public static void VerifierCibleExclusive(bool produitRenseigne, string? categorie)
    {
        if (produitRenseigne != string.IsNullOrWhiteSpace(categorie))
            throw new RegleMetierException("Choisissez un produit ou une catégorie (l’un ou l’autre).", "cible_remise");
    }

    public static void VerifierPourcentage(decimal pourcentage)
    {
        if (pourcentage < 0)
            throw new RegleMetierException("La remise ne peut pas être négative : saisissez un nombre entier de 1 à 100 %.", "pourcentage_remise");
        if (pourcentage is < 1 or > 100 || pourcentage != Math.Truncate(pourcentage))
            throw new RegleMetierException("La remise est un nombre entier de 1 à 100 %.", "pourcentage_remise");
    }

    /// <summary>Fin au plus tôt le jour du début ; si <paramref name="aujourdhui"/> est donné, fin non passée (saisie locale, publication).</summary>
    public static void VerifierPeriode(DateOnly debut, DateOnly fin, DateOnly? aujourdhui)
    {
        if (fin < debut)
            throw new RegleMetierException("La date de fin doit être le jour du début ou après.", "fin_remise");
        if (aujourdhui is { } jour && fin < jour)
            throw new RegleMetierException("La date de fin est déjà passée.", "fin_remise");
    }

    /// <summary>Motif rogné, null s'il est vide.</summary>
    public static string? NettoyerMotif(string? motif)
    {
        var m = string.IsNullOrWhiteSpace(motif) ? null : motif.Trim();
        if (m is { Length: > MotifMax }) throw new RegleMetierException($"Motif trop long ({MotifMax} caractères au plus).", "motif_remise");
        return m;
    }

    public static void VerifierProduit(string nom, bool remboursable)
    {
        if (remboursable)
            throw new RegleMetierException($"{nom} est remboursable : jamais de remise automatique (une réduction sur un médicament remboursable est un don partiel, accordé à la caisse).", "cible_remise");
    }

    /// <summary>Deux périodes (bornes incluses) qui se recouvrent en au moins un jour.</summary>
    public static bool Chevauche(DateOnly debutA, DateOnly finA, DateOnly debutB, DateOnly finB) => debutA <= finB && finA >= debutB;

    public static void ExigerSansChevauchement(string cible, bool chevauche)
    {
        if (chevauche)
            throw new RegleMetierException($"{cible} a déjà une remise automatique sur une partie de cette période : modifiez-la ou arrêtez-la d’abord.", "debut_remise");
    }
}
