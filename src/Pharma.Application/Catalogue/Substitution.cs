using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

// ---- Substitution (lot 7, M3b, script 116) ---------------------------------------
// « Équivalent exact seulement » (même DCI, dosage et forme) activé par défaut ; le titulaire peut le désactiver pour retrouver
// les propositions « autre dosage ou autre forme, à valider ». Génériques fabriqués en Algérie mis en tête. Liste blanche : produits
// autorisés par le pharmacien en remplacement d'un produit donné.

/// <summary>EquivalentExactSeulement : vrai par défaut (critère d'acceptation M3).</summary>
public sealed record ParametreSubstitution(bool EquivalentExactSeulement = true, DateTime? ModifieLe = null, string? ModifiePar = null);

public sealed record LienListeBlanche(long ProduitId, string Produit, long RemplacantId, string Remplacant, DateTime CreeLe, string? CreePar);

/// <summary>« Fabriqué en Algérie » : Saisie (fiche du produit, null = non saisi), Nomenclature (pays de la nomenclature importée).</summary>
public sealed record ProduitFabrication(long ProduitId, string Produit, string? Laboratoire, bool? Saisie, bool? Nomenclature)
{
    public bool? Retenue => Saisie ?? Nomenclature;
}

/// <summary>Remplaçant autorisé (liste blanche) d'un produit, avec son prix servi et son stock.</summary>
public sealed record RemplacantAutorise(long ProduitId, ProduitPrixServi Remplacant);

public interface ISubstitution
{
    Task<ParametreSubstitution> ParametreAsync(CancellationToken ct = default);
    Task EnregistrerParametreAsync(bool equivalentExactSeulement, long utilisateurId, CancellationToken ct = default);
    Task<Page<LienListeBlanche>> ListeBlancheAsync(string? recherche, DemandePage page, CancellationToken ct = default);
    /// <summary>Faux si le lien existait déjà.</summary>
    Task<bool> AjouterListeBlancheAsync(long produitId, long remplacantId, long utilisateurId, CancellationToken ct = default);
    Task<bool> RetirerListeBlancheAsync(long produitId, long remplacantId, CancellationToken ct = default);
    Task<bool> ProduitExisteAsync(long produitId, CancellationToken ct = default);
    Task<Page<ProduitFabrication>> FabricationAsync(string? recherche, bool? locale, DemandePage page, CancellationToken ct = default);
    Task<bool> ModifierFabricationAsync(long produitId, bool? fabricationLocale, CancellationToken ct = default);
}

/// <summary>
/// Choix des remplaçants (règle pure) : candidats = équivalents exacts (même DCI, dosage, forme) et produits de la liste blanche, en
/// stock ; à défaut, et seulement si « équivalent exact seulement » est désactivé, la même DCI dans un autre dosage ou une autre forme
/// (à valider par le pharmacien). Ordre : fabriqué en Algérie d'abord, puis le moins cher (prix servi), puis le plus de stock.
/// </summary>
public static class RegleSubstitution
{
    public static IReadOnlyList<ProduitAlternatif> Choisir(IEnumerable<ProduitAlternatif> tous, bool equivalentExactSeulement)
    {
        var liste = tous.Where(a => a.Stock > 0).GroupBy(a => a.ProduitId)
            .Select(g => g.First() with { ListeBlanche = g.Any(x => x.ListeBlanche), Exact = g.Any(x => x.Exact) }).ToList();
        var valides = liste.Where(a => a.Exact || a.ListeBlanche).ToList();
        var retenus = valides.Count > 0 || equivalentExactSeulement ? valides : liste;
        return Ordonner(retenus).ToList();
    }

    public static IEnumerable<ProduitAlternatif> Ordonner(IEnumerable<ProduitAlternatif> liste) =>
        liste.OrderByDescending(a => a.FabricationLocale).ThenBy(a => a.PrixPublic).ThenByDescending(a => a.Stock).ThenBy(a => a.ProduitId);
}

public sealed class ConsulterParametreSubstitution(ISubstitution substitution)
{
    public Task<ParametreSubstitution> ExecuterAsync(CancellationToken ct = default) => substitution.ParametreAsync(ct);
}

public sealed class ModifierParametreSubstitution(ISubstitution substitution, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(bool equivalentExactSeulement, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler la substitution", ct);
        await substitution.EnregistrerParametreAsync(equivalentExactSeulement, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "parametre_substitution", "parametre_substitution", 1,
            new Dictionary<string, object?> { ["equivalent_exact_seulement"] = equivalentExactSeulement }, Permissions.AdminParametres, ct);
    }
}

public sealed class GererListeBlanche(ISubstitution substitution, IPermissionService droits, IJournal journal)
{
    public Task<Page<LienListeBlanche>> PageAsync(string? recherche, DemandePage page, CancellationToken ct = default) =>
        substitution.ListeBlancheAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), page, ct);

    public async Task AjouterAsync(long produitId, long remplacantId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler la liste blanche de substitution", ct);
        if (produitId == remplacantId) throw new RegleMetierException("Un produit ne peut pas se remplacer lui-même.", "remplacant");
        if (!await substitution.ProduitExisteAsync(produitId, ct)) throw new RegleMetierException("Choisissez le produit à remplacer.", "produit");
        if (!await substitution.ProduitExisteAsync(remplacantId, ct)) throw new RegleMetierException("Choisissez le produit autorisé en remplacement.", "remplacant");
        if (!await substitution.AjouterListeBlancheAsync(produitId, remplacantId, utilisateurId, ct))
            throw new RegleMetierException("Ce remplacement est déjà dans la liste blanche.");
        await journal.EcrireAsync(utilisateurId, "liste_blanche_ajout", "produit", produitId,
            new Dictionary<string, object?> { ["remplacant_id"] = remplacantId }, Permissions.AdminParametres, ct);
    }

    public async Task RetirerAsync(long produitId, long remplacantId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler la liste blanche de substitution", ct);
        if (!await substitution.RetirerListeBlancheAsync(produitId, remplacantId, ct))
            throw new RegleMetierException("Ce remplacement n’est pas dans la liste blanche.");
        await journal.EcrireAsync(utilisateurId, "liste_blanche_retrait", "produit", produitId,
            new Dictionary<string, object?> { ["remplacant_id"] = remplacantId }, Permissions.AdminParametres, ct);
    }
}

/// <summary>« Fabriqué en Algérie » : saisie du pharmacien (null = reprendre la nomenclature nationale importée).</summary>
public sealed class GererFabricationLocale(ISubstitution substitution, IPermissionService droits, IJournal journal)
{
    public Task<Page<ProduitFabrication>> PageAsync(string? recherche, bool? locale, DemandePage page, CancellationToken ct = default) =>
        substitution.FabricationAsync(string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim(), locale, page, ct);

    public async Task ModifierAsync(long produitId, bool? fabricationLocale, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "indiquer un produit fabriqué en Algérie", ct);
        if (!await substitution.ModifierFabricationAsync(produitId, fabricationLocale, ct)) throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, "fabrication_locale", "produit", produitId,
            new Dictionary<string, object?> { ["fabrication_locale"] = fabricationLocale }, Permissions.AdminParametres, ct);
    }
}
