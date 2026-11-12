using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Alertes;
using Pharma.Application.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class AlertesEndpoints
{
    private const string Introuvable = "Alerte introuvable ou déjà traitée : actualisez la liste.";

    public static void MapAlertes(this RouteGroupBuilder api)
    {
        api.MapGet("/alertes", async (ListerAlertesOuvertes lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(user.UtilisateurId(), ct)).Select(a => a.VersDto())))
           .ExigerPermission(Permissions.StockConsulter);

        // Liste paginée et filtrée par le serveur ; tuiles et onglets comptés sur toutes les alertes ouvertes
        api.MapGet("/alertes/page", async (string? filtre, int? page, int? taille, ListerAlertesOuvertes lister, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await lister.PageAsync(user.UtilisateurId(), filtre, DemandePage.Lire(page, taille), ct);
                return Results.Ok(new AlertesPageDto(
                    new PageDto<AlerteDto>(p.Page.Lignes.Select(a => a.VersDto()).ToList(), p.Page.Total, p.Page.Numero, p.Page.Taille),
                    p.ParFiltre.ToDictionary(), p.ParType.ToDictionary(), p.ValeurARisque));
            })
           .ExigerPermission(Permissions.StockConsulter);

        // Pastille du bandeau : le nombre seulement (avant : toute la liste, à chaque navigation)
        // Péremption à 30, 60 et 180 jours ; règlements grossistes à préparer (avec le droit de régler)
        api.MapGet("/alertes/indicateurs", async (ObtenirIndicateursAlertes obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var i = await obtenir.ExecuterAsync(user.UtilisateurId(), ct);
                return Results.Ok(new IndicateursAlertesDto(
                    i.Peremption.Select(h => new HorizonPeremptionDto(h.Jours, h.Lots, h.Boites, h.Valeur)).ToList(), i.ReglementsAPreparer, i.MontantAPreparer));
            })
           .ExigerPermission(Permissions.StockConsulter);

        api.MapGet("/alertes/urgentes", async (ListerAlertesOuvertes lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new CompteDto(await lister.CompterUrgentesAsync(user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapPost("/alertes/generer", async (GenererAlertes generer, CancellationToken ct) =>
                Results.Ok(new { misesAJour = await generer.ExecuterAsync(ct) }))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPost("/alertes/{id:long}/reporter", async (long id, ReporterAlerteRequete req, ReporterAlerte reporter, ClaimsPrincipal user, CancellationToken ct) =>
                await reporter.ExecuterAsync(id, req.JusquAu, user.UtilisateurId(), ct)
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
