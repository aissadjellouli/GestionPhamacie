using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Pilotage;

namespace Pharma.Api.Endpoints;

/// <summary>Tableau de bord du titulaire (onglet « Pilotage » du mobile) : lecture seule, réservé au titulaire.</summary>
internal static class PilotageEndpoints
{
    public static void MapPilotage(this RouteGroupBuilder api) =>
        api.MapGet("/pilotage", async (ObtenirPilotage obtenir, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await obtenir.ExecuterAsync(user.UtilisateurId(), ct)))
           .ExigerTitulaire();
}
