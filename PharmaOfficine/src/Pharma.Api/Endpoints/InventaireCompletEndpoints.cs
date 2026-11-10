using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Administration;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Inventaire complet : avancement visible de qui compte ; lancement, clôture, annulation et état réservés au titulaire.</summary>
internal static class InventaireCompletEndpoints
{
    public static void MapInventaireComplet(this RouteGroupBuilder api)
    {
        var g = api.MapGroup("/inventaire-complet");

        g.MapGet("", async (ConsulterInventaireComplet c, CancellationToken ct) =>
            {
                var a = await c.EnCoursAsync(ct);
                var historique = await c.HistoriqueAsync(ct);
                return Results.Ok(new InventaireCompletDto(
                    a is null ? null : VersDto(a.Campagne),
                    a?.Rayons.Select(r => new RayonCampagneDto(r.Rayon, r.NombreLots, r.InventaireId, r.Statut, r.EcartsAValider)).ToList() ?? [],
                    historique.Select(VersDto).ToList()));
            })
         .ExigerPermission(Permissions.StockConsulter);

        g.MapPost("", async (CommencerCampagneRequete r, GererInventaireComplet gerer, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok(new IdentifiantDto(await gerer.CommencerAsync(r.Mode, user.UtilisateurId(), ct))));

        g.MapPost("/{id:long}/cloture", async (long id, CloturerCampagneRequete r, GererInventaireComplet gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.CloturerAsync(id, r.Commentaire, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        g.MapPost("/{id:long}/annulation", async (long id, GererInventaireComplet gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.AnnulerAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        g.MapGet("/{id:long}/etat", async (long id, GererInventaireComplet gerer, IDocumentsInventaire pdf, IPharmacie pharmacie, ClaimsPrincipal user,
                CancellationToken ct) =>
            {
                var (contenu, nom) = await gerer.EtatPdfAsync(id, pdf, (await pharmacie.LireAsync(ct)).Nom, user.UtilisateurId(), ct);
                return Results.File(contenu, "application/pdf", nom);
            });
    }

    private static CampagneInventaireDto VersDto(CampagneInventaire c) => new(c.Id, c.Mode, c.Statut, c.CommenceLe, c.CommencePar, c.ClotureLe, c.CloturePar,
        c.NombreLots, c.NombreBoites, c.ValeurStock, c.Commentaire);
}
