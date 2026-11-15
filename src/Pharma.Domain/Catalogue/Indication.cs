using Pharma.Domain.Commun;

namespace Pharma.Domain.Catalogue;

/// <summary>
/// Indication thérapeutique (script 104, 2026-11-15) : « Toux », « Fièvre »… associée aux produits (plusieurs par produit) pour la
/// recherche au comptoir. Jamais supprimée : désactivée, elle n'est plus proposée. NbProduits : produits associés.
/// </summary>
public sealed record Indication(long Id, string Libelle, bool Actif, int NbProduits = 0);

/// <summary>Libellé d'une indication : 1 à 80 caractères, unique sans tenir compte de la casse ; association : indications distinctes.</summary>
public static class RegleIndication
{
    public const int LibelleMax = 80;
    public const int ParProduitMax = 20;

    public static string VerifierLibelle(string? libelle)
    {
        var l = string.Join(' ', (libelle ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (l.Length is 0 or > LibelleMax) throw new RegleMetierException($"Libellé de l’indication : 1 à {LibelleMax} caractères.", "libelle");
        return l;
    }

    /// <summary>
    /// Indications d'un produit : sans doublon ; chacune doit exister ; une indication désactivée ne peut être ajoutée (celles déjà
    /// associées peuvent rester).
    /// </summary>
    public static IReadOnlyList<long> VerifierAssociation(IEnumerable<long>? demandees, IReadOnlyCollection<Indication> connues,
        IReadOnlyCollection<long> actuelles)
    {
        var ids = (demandees ?? []).Distinct().ToList();
        if (ids.Count > ParProduitMax) throw new RegleMetierException($"{ParProduitMax} indications au plus par produit.", "indications");
        foreach (var id in ids)
        {
            var i = connues.FirstOrDefault(x => x.Id == id) ?? throw new RegleMetierException("Indication introuvable.", "indications");
            if (!i.Actif && !actuelles.Contains(id))
                throw new RegleMetierException($"L’indication « {i.Libelle} » est désactivée : elle ne peut pas être ajoutée.", "indications");
        }
        return ids;
    }
}
