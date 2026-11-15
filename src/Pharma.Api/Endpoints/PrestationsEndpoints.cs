using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Types de prestation (2026-11-15, script 105) : liste paginée et filtrée (droit de vendre : recherche à la caisse) ; ajout et modification
/// avec le droit de créer au catalogue (catalogue.creer, D-9). Jamais de suppression.
/// </summary>
internal static class PrestationsEndpoints
{
    public static void MapPrestations(this RouteGroupBuilder api)
    {
        api.MapGet("/types-prestation", async (bool? actif, string? q, int? page, int? taille, TypesPrestation types,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await types.ListerAsync(actif, q, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<TypePrestationDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/types-prestation", async (TypePrestationSaisie s, TypesPrestation types, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await types.EnregistrerAsync(null, s.Code, s.Libelle, s.Prix, s.TauxTva, s.Actif, user.UtilisateurId(), ct, s.Remboursable, s.TarifReference))))
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();

        api.MapPut("/types-prestation/{id:long}", async (long id, TypePrestationSaisie s, TypesPrestation types, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await types.EnregistrerAsync(id, s.Code, s.Libelle, s.Prix, s.TauxTva, s.Actif, user.UtilisateurId(), ct, s.Remboursable, s.TarifReference))))
           .ExigerPermission(Permissions.CatalogueCreer)
           .GererErreursMetier();
    }

    private static TypePrestationDto Dto(TypePrestation t) => new(t.Id, t.Code, t.Libelle, t.Prix, t.TauxTva, t.Actif, t.Remboursable, t.TarifReference);
}
