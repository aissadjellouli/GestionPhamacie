using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

// ---- Indications thérapeutiques (2026-11-15, modèle Pharma, lot 5, script 104) ----

public interface IIndications
{
    Task<Page<Indication>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<IReadOnlyList<Indication>> ParIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default);
    /// <summary>Crée (id null) ou modifie l'indication ; libellé déjà pris : RegleMetierException (409).</summary>
    Task<long> EnregistrerAsync(long? id, string libelle, bool actif, CancellationToken ct = default);
    /// <summary>Indications associées au produit (actives ou non), par libellé.</summary>
    Task<IReadOnlyList<Indication>> DuProduitAsync(long produitId, CancellationToken ct = default);
    /// <summary>Remplace les indications du produit ; produit inconnu : RegleMetierException.</summary>
    Task RemplacerDuProduitAsync(long produitId, IReadOnlyList<long> indicationIds, CancellationToken ct = default);
}

/// <summary>
/// Indications : liste paginée et filtrée, ajout, libellé, désactivation (jamais de suppression) et association à un produit, avec le
/// droit de créer au catalogue (catalogue.creer). Journalisé (indication_creee / indication_modifiee / produit_indications).
/// </summary>
public sealed class Indications(IIndications indications, IPermissionService droits, IJournal journal)
{
    public async Task<Page<Indication>> ListerAsync(bool? actif, string? recherche, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "voir les indications", ct);
        var q = string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim()[..Math.Min(recherche.Trim().Length, 60)];
        return await indications.ListerAsync(actif, q, page, ct);
    }

    public async Task<Indication> EnregistrerAsync(long? id, string? libelle, bool actif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "gérer les indications", ct);
        var l = RegleIndication.VerifierLibelle(libelle);
        Indication? avant = null;
        if (id is { } i)
            avant = (await indications.ParIdsAsync([i], ct)).FirstOrDefault() ?? throw new RegleMetierException("Indication introuvable.");
        var nouvelId = await indications.EnregistrerAsync(id, l, id is null || actif, ct);
        await journal.EcrireAsync(utilisateurId, id is null ? "indication_creee" : "indication_modifiee", "indication", nouvelId,
            new Dictionary<string, object?>
            {
                ["libelle"] = l, ["actif"] = id is null || actif,
                ["avant"] = avant is null ? null : new { libelle = avant.Libelle, actif = avant.Actif }
            }, Permissions.CatalogueCreer, ct);
        return (await indications.ParIdsAsync([nouvelId], ct)).First();
    }

    public async Task<IReadOnlyList<Indication>> DuProduitAsync(long produitId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "voir les indications d’un produit", ct);
        return await indications.DuProduitAsync(produitId, ct);
    }

    /// <summary>Fiche produit : remplace les indications associées (ajouts et retraits journalisés).</summary>
    public async Task AssocierAsync(long produitId, IReadOnlyList<long>? indicationIds, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "associer des indications à un produit", ct);
        var actuelles = await indications.DuProduitAsync(produitId, ct);
        var demandees = (indicationIds ?? []).Distinct().ToList();
        var connues = demandees.Count == 0 ? [] : await indications.ParIdsAsync(demandees, ct);
        var ids = RegleIndication.VerifierAssociation(demandees, connues, actuelles.Select(a => a.Id).ToList());
        var ajoutees = ids.Where(x => actuelles.All(a => a.Id != x)).ToList();
        var retirees = actuelles.Where(a => !ids.Contains(a.Id)).ToList();
        if (ajoutees.Count == 0 && retirees.Count == 0) return;
        await indications.RemplacerDuProduitAsync(produitId, ids, ct);
        await journal.EcrireAsync(utilisateurId, "produit_indications", "produit", produitId,
            new Dictionary<string, object?>
            {
                ["ajoutees"] = connues.Where(c => ajoutees.Contains(c.Id)).Select(c => c.Libelle).ToList(),
                ["retirees"] = retirees.Select(r => r.Libelle).ToList()
            }, Permissions.CatalogueCreer, ct);
    }
}
