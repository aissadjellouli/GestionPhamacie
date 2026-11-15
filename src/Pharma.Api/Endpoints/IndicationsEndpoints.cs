using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Indications thérapeutiques (2026-11-15, script 104) : liste paginée et filtrée, ajout, modification, désactivation et association
/// à un produit avec le droit de créer au catalogue (catalogue.creer). Jamais de suppression. Recherche au comptoir : api/caisse/produits.
/// </summary>
internal static class IndicationsEndpoints
{
    public static void MapIndications(this RouteGroupBuilder api)
    {
        api.MapGet("/indications", async (bool? actif, string? q, int? page, int? taille, Indications indications, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var p = await indications.ListerAsync(actif, q, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<IndicationDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();

        api.MapPost("/indications", async (IndicationSaisie s, Indications indications, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await indications.EnregistrerAsync(null, s.Libelle, true, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();

        api.MapPut("/indications/{id:long}", async (long id, IndicationSaisie s, Indications indications, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await indications.EnregistrerAsync(id, s.Libelle, s.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();

        api.MapGet("/produits/{id:long}/indications", async (long id, Indications indications, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await indications.DuProduitAsync(id, user.UtilisateurId(), ct)).Select(Dto).ToList()))
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();

        api.MapPut("/produits/{id:long}/indications", async (long id, IndicationsProduitRequete r, Indications indications, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                await indications.AssocierAsync(id, r.IndicationIds, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();
    }

    private static IndicationDto Dto(Indication i) => new(i.Id, i.Libelle, i.Actif, i.NbProduits);
}
