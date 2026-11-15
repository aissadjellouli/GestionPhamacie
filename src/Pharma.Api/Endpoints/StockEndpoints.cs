using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Shared;

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

        // ---- Entrées de stock (lots) et corrections tracées (script 103) ----
        api.MapGet("/stock/entrees", async (string? q, bool? epuises, int? page, int? taille, ConsulterEntreesStock consulter, CancellationToken ct) =>
                Results.Ok((await consulter.PageAsync(new FiltreEntreesStock(q, epuises ?? false), DemandePage.Lire(page, taille), ct)) is var p
                    ? new PageDto<EntreeStockDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille) : null))
           .ExigerPermission(Permissions.StockConsulter);

        api.MapGet("/lots/{id:long}/corrections", async (long id, ConsulterEntreesStock consulter, CancellationToken ct) =>
                Results.Ok((await consulter.CorrectionsAsync(id, ct))
                    .Select(c => new CorrectionLotDto(c.Id, c.Champ, c.AncienneValeur, c.NouvelleValeur, c.Motif, c.CorrigeLe, c.CorrigePar)).ToList()))
           .ExigerPermission(Permissions.StockConsulter);

        // Droit « Corriger une entrée de stock » ; jamais par le code comptoir d'un collègue (hors liste blanche)
        api.MapPost("/lots/{id:long}/corrections", async (long id, CorrectionLotSaisie req, CorrigerEntreeStock corriger, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await corriger.ExecuterAsync(id,
                    new DemandeCorrectionLot(req.Ppa, req.NumeroLot, req.DatePeremption, req.Shp, req.QuantiteRestante, req.Motif), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.StockCorrigerEntree);
    }

    private static EntreeStockDto VersDto(EntreeStock e) => new(e.LotId, e.ProduitId, e.Produit, e.NumeroLot, e.DatePeremption, e.Quantite,
        e.Ppa, e.Shp, e.Fournisseur, e.DateReception, e.Bloque, e.Corrections);
}
