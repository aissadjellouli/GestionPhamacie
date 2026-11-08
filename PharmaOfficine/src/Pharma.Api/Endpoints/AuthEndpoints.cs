using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class AuthEndpoints
{
    public static void MapAuth(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/connexion", async (ConnexionRequete req, SeConnecter seConnecter, CancellationToken ct) =>
            await seConnecter.ExecuterAsync(req.Identifiant, req.MotDePasse, ct) is { } session
                ? Results.Ok(session.VersDto())
                : Results.Json(new ErreurDto("Identifiant ou mot de passe incorrect."), statusCode: 401));

        // Droits de l'employé connecté : le poste s'en sert pour griser ce qui est refusé (le serveur revérifie toujours)
        app.MapGet("/api/auth/permissions", async (ListerMesPermissions lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await lister.ExecuterAsync(user.UtilisateurId(), ct)))
           .RequireAuthorization();
    }
}
