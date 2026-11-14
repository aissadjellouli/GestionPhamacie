using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;

namespace Pharma.Api.Endpoints;

internal static class StockEndpoints
{
    public static void MapStock(this RouteGroupBuilder api)
    {
        // Valeur du stock au prix d'achat : seulement avec le droit « rapports.marges » (titulaire : toujours)
        api.MapGet("/stock", async (ConsulterStock consulter, IPermissionService droits, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await consulter.ExecuterAsync(ct, await droits.VoitPrixAchatAsync(user.UtilisateurId(), ct))).VersDto()))
           .ExigerPermission(Permissions.StockConsulter);

        // Tableau de stock paginé et filtré par le serveur (recherche, filtre, rayon) ; propositions pour aider la saisie
        api.MapGet("/stock/page", async (int? page, int? taille, string? q, string? filtre, string? rayon, ConsulterStock consulter,
                IPermissionService droits, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await consulter.PageAsync(new FiltreStock(q, filtre, rayon), DemandePage.Lire(page, taille), ct,
                    await droits.VoitPrixAchatAsync(user.UtilisateurId(), ct))).VersDto()))
           .ExigerPermission(Permissions.StockConsulter);
    }
}
