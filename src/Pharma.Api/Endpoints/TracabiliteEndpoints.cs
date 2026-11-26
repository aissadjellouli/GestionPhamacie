using Pharma.Application.Offre;
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
           .ExigerPermission(Permissions.StockConsulter).ExigerPack(PacksOffre.Inventaire);

        api.MapPost("/tracabilite/rappels", async (RappelLotRequete req, RappelerLot rappeler,
                ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await rappeler.ExecuterAsync(req.ProduitId, req.NumeroLot, req.Motif, user.UtilisateurId(), ct)).VersDto()))
           .ExigerPermission(Permissions.StockDeclarerPerime);

        // État d'un lot et patients concernés, sans rien bloquer (lot déjà rappelé) : même droit que le rappel
        api.MapGet("/tracabilite/rappels", async (long produit, string? lot, ConsulterRappelLot consulter, CancellationToken ct) =>
                Results.Ok((await consulter.ExecuterAsync(produit, lot, ct)).VersDto()))
           .ExigerPermission(Permissions.StockDeclarerPerime).ExigerPack(PacksOffre.Inventaire);

        // Numéros de lot proposés au rappel (2026-11-18) : tous les lots du produit, épuisés compris, 10 au plus ; même droit que le rappel
        api.MapGet("/tracabilite/produits/{id:long}/lots", async (long id, string? q, ProposerLotsRappel proposer, CancellationToken ct) =>
                Results.Ok((await proposer.ExecuterAsync(id, q, ct)).Select(l => l.VersDto()).ToList()))
           .ExigerPermission(Permissions.StockDeclarerPerime);

        api.MapGet("/tracabilite/boites/{code}", async (string code, ConsulterParcoursBoite consulter, ClaimsPrincipal user, CancellationToken ct) =>
                await consulter.ExecuterAsync(code, user.UtilisateurId(), ct) is { } parcours
                    ? Results.Ok(parcours.VersDto())
                    : Results.NotFound(new ErreurDto("Boîte inconnue : vérifiez le code ou l'étiquette.")))
           .ExigerPermission(Permissions.StockConsulter).ExigerPack(PacksOffre.Inventaire);
    }
}
