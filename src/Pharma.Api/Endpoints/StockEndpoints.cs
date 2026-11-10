using Pharma.Api.Securite;
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
    }
}
