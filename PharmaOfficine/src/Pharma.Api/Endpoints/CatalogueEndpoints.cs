using Pharma.Api.Securite;
using Pharma.Application.Catalogue;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class CatalogueEndpoints
{
    public static void MapCatalogue(this RouteGroupBuilder api)
    {
        api.MapGet("/produits", async (string q, int? limite, RechercherProduits rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, limite, ct)).Select(p => p.VersDto())))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapGet("/produits/{id:long}/lot-fefo", async (long id, ObtenirLotFefo obtenir, CancellationToken ct) =>
            {
                var resultat = await obtenir.ExecuterAsync(id, ct);
                return resultat.Lot is { } lot
                    ? Results.Ok(lot.VersDto(resultat.Ecartes))
                    : Results.NotFound(new AucunLotDto(
                        "Aucun lot vendable : rupture, péremption ou blocage.",
                        resultat.Ecartes.Select(e => e.VersDto()).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/produits/{id:long}/alternatives", async (long id, ListerAlternatives lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(id, ct)).Select(a => a.VersDto())))
           .ExigerPermission(Permissions.CaisseVendre);
    }
}
