using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Caisse;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;
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

        // L'employé connecté change son mot de passe (obligatoire avec un mot de passe provisoire) : nouveau jeton, droits du rôle
        app.MapPost("/api/auth/mot-de-passe", async (ChangementMotDePasseRequete req, ChangerMonMotDePasse changer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await changer.ExecuterAsync(user.UtilisateurId(), req.Actuel, req.Nouveau, req.Confirmation, ct)).VersDto()))
           .RequireAuthorization()
           .GererErreursMetier();

        // Changement d'utilisateur au comptoir : seulement sur un poste dont la caisse est ouverte ; jeton limité au comptoir
        app.MapPost("/api/auth/connexion-code", async (ConnexionCodeRequete req, SeConnecterParCode seConnecter, HttpRequest requete, CancellationToken ct) =>
            Results.Ok((await seConnecter.ExecuterAsync(req.Identifiant, req.Code, requete.Poste(), ct)).VersDto()));

        // Écran de connexion : la caisse du poste est-elle ouverte (onglet « Code comptoir ») ? Aucun nom ni identifiant
        // n'est donné sans connexion : l'employé tape son identifiant.
        app.MapGet("/api/auth/comptoir", async (ISessionsCaisse sessions, HttpRequest requete, CancellationToken ct) =>
            {
                var poste = requete.Poste();
                var ouverte = !string.IsNullOrWhiteSpace(poste) && await sessions.SessionDuPosteAsync(poste.Trim(), ct) is not null;
                return Results.Ok(new ComptoirDto(ouverte));
            });

        // Droits de l'employé connecté : le poste s'en sert pour griser ce qui est refusé (le serveur revérifie toujours)
        app.MapGet("/api/auth/permissions", async (ListerMesPermissions lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await lister.ExecuterAsync(user.UtilisateurId(), ct)))
           .RequireAuthorization();

        // Affichage du poste choisi par l'employé (clair, sombre, auto) : aucun droit particulier, code comptoir compris
        app.MapGet("/api/auth/affichage", async (LireAffichage lire, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new AffichageDto(await lire.ExecuterAsync(user.UtilisateurId(), ct))))
           .RequireAuthorization();

        app.MapPut("/api/auth/affichage", async (AffichageDto req, ChoisirAffichage choisir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await choisir.ExecuterAsync(user.UtilisateurId(), req.Affichage, ct);
                return Results.NoContent();
            })
           .RequireAuthorization();

        // L'employé choisit son code comptoir (connexion par mot de passe exigée)
        app.MapPut("/api/auth/code-comptoir", async (CodeComptoirRequete req, DefinirCodeComptoir definir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await definir.ExecuterAsync(user.UtilisateurId(), user.ParCodeComptoir(), req.Code, ct);
                return Results.NoContent();
            })
           .RequireAuthorization();
    }
}
