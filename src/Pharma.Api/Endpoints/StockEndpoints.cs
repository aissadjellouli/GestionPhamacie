using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;

namespace Pharma.Api.Endpoints;

internal static class StockEndpoints
{
    public static void MapStock(this RouteGroupBuilder api)
    {
        api.MapGet("/stock", async (ConsulterStock consulter, CancellationToken ct) =>
                Results.Ok((await consulter.ExecuterAsync(ct)).VersDto()))
           .ExigerPermission(Permissions.StockConsulter);

        // Tableau de stock paginé et filtré par le serveur (recherche, filtre, rayon) ; propositions pour aider la saisie
        api.MapGet("/stock/page", async (int? page, int? taille, string? q, string? filtre, string? rayon, ConsulterStock consulter, CancellationToken ct) =>
                Results.Ok((await consulter.PageAsync(new FiltreStock(q, filtre, rayon), DemandePage.Lire(page, taille), ct)).VersDto()))
           .ExigerPermission(Permissions.StockConsulter);
    }
}
