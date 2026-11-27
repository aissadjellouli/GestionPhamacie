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
        // PC non autorisé (décision du titulaire du 2026-11-27) : seul le titulaire se connecte, pour l'Administration seulement (jeton marqué,
        // aucun poste lié : ce PC n'est pas un poste) ; tout autre employé est refusé (403, message clair)
        app.MapPost("/api/auth/connexion", async (ConnexionRequete req, SeConnecter seConnecter, HttpRequest requete, CancellationToken ct) =>
        {
            var pcNonProuve = AccesAdministrationSeule.PcNonProuve(requete.HttpContext);
            return await seConnecter.ExecuterAsync(req.Identifiant, req.MotDePasse, ct, pcNonProuve ? null : requete.Poste(), titulaireSeulement: pcNonProuve) is { } session
                ? Results.Ok(session.VersDto() with { AdministrationSeule = pcNonProuve })
                : Results.Json(new ErreurDto("Identifiant ou mot de passe incorrect."), statusCode: 401);
        });

        // L'employé connecté change son mot de passe (obligatoire avec un mot de passe provisoire) : nouveau jeton, droits du rôle
        app.MapPost("/api/auth/mot-de-passe", async (ChangementMotDePasseRequete req, ChangerMonMotDePasse changer, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
                Results.Ok((await changer.ExecuterAsync(user.UtilisateurId(), req.Actuel, req.Nouveau, req.Confirmation, ct,
                    AccesAdministrationSeule.Demande(requete.HttpContext) ? null : requete.Poste())).VersDto()
                    with { AdministrationSeule = AccesAdministrationSeule.Demande(requete.HttpContext) }))
           .ExigerConnexion(motDePasseProvisoire: true)
           .GererErreursMetier();

        // Changement d'utilisateur au comptoir : seulement sur un poste dont la caisse est ouverte ; jeton limité au comptoir
        app.MapPost("/api/auth/connexion-code", async (ConnexionCodeRequete req, SeConnecterParCode seConnecter, HttpRequest requete, CancellationToken ct) =>
            Results.Ok((await seConnecter.ExecuterAsync(req.Identifiant, req.Code, requete.Poste(), ct)).VersDto()));

        // Droits de l'employé connecté : le poste s'en sert pour griser ce qui est refusé (le serveur revérifie toujours)
        app.MapGet("/api/auth/permissions", async (ListerMesPermissions lister, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await lister.ExecuterAsync(user.UtilisateurId(), ct)))
           .ExigerConnexion(motDePasseProvisoire: true, superAdmin: true);

        // Affichage du poste choisi par l'employé (clair, sombre, auto) : aucun droit particulier, code comptoir compris
        app.MapGet("/api/auth/affichage", async (LireAffichage lire, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new AffichageDto(await lire.ExecuterAsync(user.UtilisateurId(), ct))))
           .ExigerConnexion(motDePasseProvisoire: true, superAdmin: true);

        app.MapPut("/api/auth/affichage", async (AffichageDto req, ChoisirAffichage choisir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await choisir.ExecuterAsync(user.UtilisateurId(), req.Affichage, ct);
                return Results.NoContent();
            })
           .ExigerConnexion(motDePasseProvisoire: true, superAdmin: true);

        // L'employé choisit son code comptoir (connexion par mot de passe exigée)
        app.MapPut("/api/auth/code-comptoir", async (CodeComptoirRequete req, DefinirCodeComptoir definir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await definir.ExecuterAsync(user.UtilisateurId(), user.ParCodeComptoir(), req.Code, ct);
                return Results.NoContent();
            })
           .ExigerConnexion();
    }
}
