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
    }
}
