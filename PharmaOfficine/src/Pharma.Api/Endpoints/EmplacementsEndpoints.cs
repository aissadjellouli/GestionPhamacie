using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Emplacements de rangement et rangement des produits.</summary>
internal static class EmplacementsEndpoints
{
    public static void MapEmplacements(this RouteGroupBuilder api)
    {
        api.MapGet("/emplacements", async (ListerEmplacements lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(e => new EmplacementDto(
                    e.Id, e.Code, e.Rayon, e.Etagere, e.Tiroir, e.Libelle, e.Zone.Code(), e.Actif, e.NombreProduits))))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapPost("/emplacements", async (SaisieEmplacementDto d, EnregistrerEmplacement enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(null, VersSaisie(d), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPut("/emplacements/{id:long}", async (long id, SaisieEmplacementDto d, EnregistrerEmplacement enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(id, VersSaisie(d), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPut("/produits/{id:long}/emplacement", async (long id, RangementDto d, RangerProduit ranger, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await ranger.ExecuterAsync(id, d.EmplacementId, d.ReserveId, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.StockAjuster);
    }

    private static SaisieEmplacement VersSaisie(SaisieEmplacementDto d) =>
        new(d.Rayon, d.Etagere, d.Tiroir, d.Libelle, RegleEmplacement.ZoneDepuisCode(d.Zone), d.Actif);
}
