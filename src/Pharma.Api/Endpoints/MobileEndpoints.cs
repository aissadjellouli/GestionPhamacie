using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Mobile;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Scanner mobile (page /mobile/) : contrôle en rayon, dates à vérifier, boîtes scannées au déballage.</summary>
internal static class MobileEndpoints
{
    public static void MapMobile(this RouteGroupBuilder api)
    {
        var mobile = api.MapGroup("/mobile");

        mobile.MapPost("/code", async (CodeRequete req, IdentifierCode identifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var f = await identifier.ExecuterAsync(req.Code, user.UtilisateurId(), ct);
                var p = f.Produit;
                return Results.Ok(new FicheCodeDto(f.Type,
                    new FicheProduitDto(p.ProduitId, p.Nom, p.Dci, p.Dosage, p.Forme, p.PrixPublic, p.Stock, p.EmplacementId, p.Emplacement,
                        p.ReserveId, p.Reserve, p.Lots.Select(l => new LotFicheDto(l.Id, l.NumeroLot, l.DatePeremption, l.DateAVerifier,
                            l.Quantite, l.Bloque, l.MotifBlocage, l.Fournisseur, l.Niveau)).ToList()),
                    f.LotId, f.StatutBoite, f.Alertes));
            })
           .ExigerPermission(Permissions.StockConsulter);

        mobile.MapPut("/lots/{id:long}/peremption", async (long id, PeremptionRequete req, ConfirmerPeremption confirmer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await confirmer.ExecuterAsync(id, req.Date, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.StockAjuster);

        mobile.MapPost("/reception/scans", async (CodeRequete req, ScannerBoiteReception scanner, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await scanner.ExecuterAsync(req.Code, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AchatsReceptionner);

        mobile.MapGet("/reception/scans", async (ListerScansReception lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(user.UtilisateurId(), ct)).Select(VersDto)))
           .ExigerPermission(Permissions.AchatsReceptionner);

        mobile.MapDelete("/reception/scans/{id:long}", async (long id, SupprimerScanReception supprimer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await supprimer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.AchatsReceptionner);

        // Réception abandonnée sur le poste : les scans importés reviennent en attente
        mobile.MapPost("/reception/scans/remettre", async (IdsRequete req, RemettreScansEnAttente remettre, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new NombreDto(await remettre.ExecuterAsync(req.Ids, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AchatsReceptionner);

        // Le poste reprend les scans dans sa réception
        mobile.MapPost("/reception/scans/importer", async (ImporterScansReception importer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await importer.ExecuterAsync(user.UtilisateurId(), ct)).Select(l =>
                    new LigneScanneeDto(l.ProduitId, l.Produit, l.Gtin, l.NumeroLot, l.Peremption, l.Quantite, l.Series, l.ScanIds))))
           .ExigerPermission(Permissions.AchatsReceptionner);
    }

    private static ScanReceptionDto VersDto(ScanReception s) =>
        new(s.Id, s.ProduitId, s.Produit, s.Gtin, s.NumeroLot, s.Peremption, s.NumeroSerie, s.ScannePar, s.ScanneLe);
}
