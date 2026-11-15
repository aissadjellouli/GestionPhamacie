using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Prestation vendue à la caisse sans stock (script 105, 2026-11-15) : prise de tension, glycémie, injection… Prix TTC et taux de TVA
/// figés sur la ligne de vente. Jamais supprimée : désactivée, elle n'est plus proposée.
/// Remboursable (script 108, 2026-11-15) : en tiers payant, part de l'organisme = taux × min(prix, tarif de référence), comme un produit ;
/// non remboursable : l'organisme ne paie rien.
/// </summary>
public sealed record TypePrestation(long Id, string Code, string Libelle, decimal Prix, decimal TauxTva, bool Actif,
    bool Remboursable = false, decimal? TarifReference = null)
{
    /// <summary>Garde-fou (2026-11-15) : une prestation à 0 DA n'est jamais vendue (serveur et caisse).</summary>
    public static string MessagePrixAFixer(string libelle) => $"{libelle} : prix à fixer dans Administration › Prestations.";
}

/// <summary>Saisie d'un type de prestation : code (1 à 20 caractères, unique sans tenir compte de la casse), libellé (1 à 120), prix ≥ 0, TVA 0 à 100 %.</summary>
public static class RegleTypePrestation
{
    public static (string Code, string Libelle) Verifier(string? code, string? libelle, decimal prix, decimal tauxTva)
    {
        var c = code?.Trim() ?? "";
        var l = libelle?.Trim() ?? "";
        if (c.Length is 0 or > 20) throw new RegleMetierException("Code de la prestation : 1 à 20 caractères.", "code");
        if (l.Length is 0 or > 120) throw new RegleMetierException("Libellé de la prestation : 1 à 120 caractères.", "libelle");
        if (prix < 0 || prix != Math.Round(prix, 2)) throw new RegleMetierException("Prix de la prestation : montant positif, au centime.", "prix");
        if (tauxTva is < 0 or > 100) throw new RegleMetierException("Taux de TVA de la prestation : de 0 à 100 %.", "taux_tva");
        return (c.ToUpperInvariant(), l);
    }

    /// <summary>
    /// Remboursement par un organisme (script 108) : une prestation remboursable exige un tarif de référence &gt; 0, au centime ;
    /// non remboursable, le tarif n'est pas gardé (null).
    /// </summary>
    public static decimal? VerifierRemboursement(bool remboursable, decimal? tarifReference)
    {
        if (!remboursable) return null;
        if (tarifReference is not { } t || t <= 0 || t != Math.Round(t, 2))
            throw new RegleMetierException("Prestation remboursable : tarif de référence obligatoire, montant positif au centime.", "tarif_reference");
        return t;
    }
}
