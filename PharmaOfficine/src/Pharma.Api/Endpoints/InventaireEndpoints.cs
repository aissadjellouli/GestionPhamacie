using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Inventaire tournant : comptage d'un rayon (stock.ajuster), validation des gros écarts (titulaire).</summary>
internal static class InventaireEndpoints
{
    public static void MapInventaire(this RouteGroupBuilder api)
    {
        var inv = api.MapGroup("/inventaires");

        inv.MapGet("", async (ObtenirTableauInventaire obtenir, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(ct);
                return Results.Ok(new TableauInventaireDto(
                    t.Rayons.Select(r => new RayonInventaireDto(r.Rayon, r.DernierInventaire, r.NombreLots, r.LotsDateAVerifier, r.EnCoursId)).ToList(),
                    t.Propose?.Rayon, new ParametresInventaireDto(t.Parametres.SeuilBoites, t.Parametres.SeuilMontant),
                    t.EcartsAValider.Select(VersDto).ToList(),
                    t.Historique.Select(h => new InventaireResumeDto(h.Id, h.Rayon, h.Statut, h.CommenceLe, h.CommencePar, h.TermineLe,
                        h.Lignes, h.Ecarts, h.AValider, h.ValeurEcarts)).ToList()));
            })
           .ExigerPermission(Permissions.StockConsulter);

        inv.MapPost("", async (CommencerInventaireRequete req, CommencerInventaire commencer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await commencer.ExecuterAsync(req.Rayon, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapGet("/{id:long}", async (long id, ObtenirInventaire obtenir, CancellationToken ct) =>
                await obtenir.ExecuterAsync(id, ct) is { } d
                    ? Results.Ok(new InventaireDto(d.Id, d.Rayon, d.Statut, d.CommenceLe, d.CommencePar, d.TermineLe,
                        d.Lignes.Select(l => new LigneInventaireDto(l.Id, l.LotId, l.Produit, l.NumeroLot, l.DatePeremption, l.DateAVerifier,
                            l.Emplacement, l.Compte, l.DateVerifiee, l.Statut, l.Theorique, l.Ecart, l.ValeurEcart)).ToList()))
                    : Results.NotFound(new ErreurDto("Inventaire introuvable.")))
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapPut("/{id:long}/lignes/{ligneId:long}", async (long id, long ligneId, ComptageRequete req, CompterLot compter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await compter.ExecuterAsync(id, ligneId, req.Compte, req.DateVerifiee, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapPost("/{id:long}/scan", async (long id, ScanInventaireRequete req, ScannerBoiteInventaire scanner, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await scanner.ExecuterAsync(id, req.Code, user.UtilisateurId(), ct);
                return Results.Ok(new ScanInventaireDto(r.LigneId, r.Produit, r.NumeroLot, r.Compte));
            })
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapPost("/{id:long}/terminer", async (long id, TerminerInventaire terminer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var b = await terminer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.Ok(new BilanInventaireDto(b.Lignes, b.Conformes, b.Ajustes, b.AValider, b.BoitesEcart, b.ValeurEcart));
            })
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapPost("/{id:long}/annuler", async (long id, AnnulerInventaire annuler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await annuler.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.StockAjuster);

        inv.MapPost("/ecarts/{ligneId:long}", async (long ligneId, DecisionEcartRequete req, DeciderEcartInventaire decider, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await decider.ExecuterAsync(ligneId, req.Valider, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        inv.MapPut("/parametres", async (ParametresInventaireDto req, ModifierParametresInventaire modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(new ParametresInventaire(req.SeuilBoites, req.SeuilMontant), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
    }

    private static EcartAValiderDto VersDto(EcartAValider e) =>
        new(e.LigneId, e.InventaireId, e.Rayon, e.Produit, e.NumeroLot, e.Theorique, e.Compte, e.Ecart, e.ValeurEcart, e.ComptePar, e.CompteLe);
}
