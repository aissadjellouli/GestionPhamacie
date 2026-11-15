using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Prestation vendue à la caisse sans stock (script 105, 2026-11-15) : prise de tension, glycémie, injection… Prix TTC et taux de TVA
/// figés sur la ligne de vente. Jamais supprimée : désactivée, elle n'est plus proposée.
/// </summary>
public sealed record TypePrestation(long Id, string Code, string Libelle, decimal Prix, decimal TauxTva, bool Actif);

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
}
