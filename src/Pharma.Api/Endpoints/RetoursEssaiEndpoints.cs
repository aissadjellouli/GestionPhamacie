using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Essai;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Retours du pharmacien pendant l'essai (page « Guide et retours ») : tout employé connecté peut en laisser.</summary>
internal static class RetoursEssaiEndpoints
{
    public static void MapRetoursEssai(this RouteGroupBuilder api)
    {
        api.MapGet("/retours-essai", async (RetoursEssai retours, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await retours.ListerAsync(user.UtilisateurId(), ct))
                    .Select(r => new RetourEssaiDto(r.Date, r.Employe, r.Ecran, r.Genre, r.Texte)).ToList()))
           .RequireAuthorization();

        api.MapPost("/retours-essai", async (NouveauRetourEssaiDto d, RetoursEssai retours, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await retours.EnregistrerAsync(d.Ecran, d.Genre, d.Texte, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .RequireAuthorization();
    }
}
