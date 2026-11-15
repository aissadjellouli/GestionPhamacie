using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

// ---- Prestations sans stock (2026-11-15, modèle Wasfa, lot 6, script 105) ----

public interface ITypesPrestation
{
    Task<Page<TypePrestation>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default);
    Task<TypePrestation?> LireAsync(long id, CancellationToken ct = default);
    /// <summary>Crée (id null) ou modifie le type ; code déjà pris : RegleMetierException (409).</summary>
    Task<long> EnregistrerAsync(long? id, string code, string libelle, decimal prix, decimal tauxTva, bool actif, CancellationToken ct = default);
}

/// <summary>
/// Types de prestation : liste paginée et filtrée (tout employé qui vend : recherche à la caisse) ; ajout, modification et désactivation
/// avec le droit de créer au catalogue (catalogue.creer, D-9). Jamais de suppression. Journalisé (type_prestation_cree / _modifie).
/// </summary>
public sealed class TypesPrestation(ITypesPrestation types, IPermissionService droits, IJournal journal)
{
    public async Task<Page<TypePrestation>> ListerAsync(bool? actif, string? recherche, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "voir les prestations", ct);
        var q = string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim()[..Math.Min(recherche.Trim().Length, 60)];
        return await types.ListerAsync(actif, q, page, ct);
    }

    public async Task<TypePrestation> EnregistrerAsync(long? id, string? code, string? libelle, decimal prix, decimal tauxTva, bool actif,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "gérer les prestations", ct);
        var (c, l) = RegleTypePrestation.Verifier(code, libelle, prix, tauxTva);
        TypePrestation? avant = null;
        if (id is { } i)
            avant = await types.LireAsync(i, ct) ?? throw new RegleMetierException("Prestation introuvable.", "prestation");
        var nouvelId = await types.EnregistrerAsync(id, c, l, prix, tauxTva, actif, ct);
        await journal.EcrireAsync(utilisateurId, id is null ? "type_prestation_cree" : "type_prestation_modifie", "type_prestation", nouvelId,
            new Dictionary<string, object?>
            {
                ["code"] = c, ["libelle"] = l, ["prix"] = prix, ["taux_tva"] = tauxTva, ["actif"] = actif,
                ["avant"] = avant is null ? null : new { code = avant.Code, libelle = avant.Libelle, prix = avant.Prix, taux_tva = avant.TauxTva, actif = avant.Actif }
            }, Permissions.CatalogueCreer, ct);
        return (await types.LireAsync(nouvelId, ct))!;
    }
}
