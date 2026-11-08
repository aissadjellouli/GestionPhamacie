using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Alertes;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class AlertesEndpoints
{
    private const string Introuvable = "Alerte introuvable ou déjà traitée : actualisez la liste.";

    public static void MapAlertes(this RouteGroupBuilder api)
    {
        api.MapGet("/alertes", async (ListerAlertesOuvertes lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(a => a.VersDto())))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapPost("/alertes/generer", async (GenererAlertes generer, CancellationToken ct) =>
                Results.Ok(new { misesAJour = await generer.ExecuterAsync(ct) }))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPost("/alertes/{id:long}/reporter", async (long id, ReporterAlerteRequete req, ReporterAlerte reporter, CancellationToken ct) =>
                await reporter.ExecuterAsync(id, req.JusquAu, ct)
                    ? Results.NoContent()
                    : Results.NotFound(new ErreurDto(Introuvable)))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapPost("/alertes/{id:long}/traiter", async (long id, TraiterAlerteRequete req, TraiterAlerte traiter,
                ClaimsPrincipal user, CancellationToken ct) =>
                await traiter.ExecuterAsync(id, req.ActionPrise, user.UtilisateurId(), ct)
                    ? Results.NoContent()
                    : Results.NotFound(new ErreurDto(Introuvable)))
           .ExigerPermission(Permissions.StockConsulter);
    }
}
