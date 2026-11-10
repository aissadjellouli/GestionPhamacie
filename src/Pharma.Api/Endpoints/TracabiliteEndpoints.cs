using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class TracabiliteEndpoints
{
    public static void MapTracabilite(this RouteGroupBuilder api)
    {
        api.MapGet("/tracabilite/etiquetage", async (ObtenirEtatEtiquetage obtenir, CancellationToken ct) =>
                Results.Ok((await obtenir.ExecuterAsync(ct)).VersDto()))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapPost("/tracabilite/rappels", async (RappelLotRequete req, RappelerLot rappeler,
                ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await rappeler.ExecuterAsync(req.ProduitId, req.NumeroLot, req.Motif, user.UtilisateurId(), ct)).VersDto()))
           .ExigerPermission(Permissions.StockDeclarerPerime);

        api.MapGet("/tracabilite/boites/{code}", async (string code, ConsulterParcoursBoite consulter, CancellationToken ct) =>
                await consulter.ExecuterAsync(code, ct) is { } parcours
                    ? Results.Ok(parcours.VersDto())
                    : Results.NotFound(new ErreurDto("Boîte inconnue : vérifiez le code ou l'étiquette.")))
           .ExigerPermission(Permissions.StockConsulter);
    }
}
