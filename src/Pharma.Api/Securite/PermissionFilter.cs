using System.Security.Claims;
using Pharma.Application.Securite;

namespace Pharma.Api.Securite;

/// <summary>Refuse l'appel si l'utilisateur n'a pas la permission (vérifiée en base à chaque appel).</summary>
public static class PermissionExtensions
{
    private const string CodeComptoirSeulement = "Connecté par code comptoir : reconnectez-vous avec votre mot de passe pour cet écran.";

    /// <summary>Identifiant de l'employé connecté (garanti par ExigerPermission).</summary>
    public static long UtilisateurId(this ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? throw new InvalidOperationException("Utilisateur non authentifié."));

    /// <summary>Une seule des permissions suffit (lecture commune à plusieurs droits).</summary>
    /// <summary>Réservé au pharmacien titulaire (rôle système), quel que soit le réglage des droits.</summary>
    /// <summary>
    /// Refus de droit : message clair et, si le titulaire l'a permis, proposition d'autorisation sur place par le code comptoir d'un
    /// employé qui a ce droit (le poste rejoue la demande avec ce code). Code faux : le motif est rendu, la proposition reste ouverte.
    /// </summary>
    public static async Task<IResult> RefusAsync(HttpContext http, string permission, string message)
    {
        var autorisation = http.RequestServices.GetRequiredService<AutorisationParCode>();
        // Droit jamais autorisé par le code d'un collègue (psychotrope ; hors comptoir sur une connexion par code) : pas de proposition
        var active = PermissionsComptoir.AutorisableSurPlace(http.User, permission) && await autorisation.ActiveeAsync(http.RequestAborted);
        var texte = autorisation.Demandee && autorisation.Erreur is { } erreur ? erreur
            : active ? $"{message} Un employé qui a ce droit peut autoriser l’action avec son code comptoir." : message;
        return Results.Json(new Pharma.Shared.ErreurDto(texte, null, active, permission), statusCode: StatusCodes.Status403Forbidden);
    }

    // Mot de passe provisoire : tout est refusé tant qu'il n'est pas changé (POST /api/auth/mot-de-passe)
    private static IResult MotDePasseAChanger() => Results.Problem(title: "Mot de passe à changer",
        detail: "Choisissez d’abord votre mot de passe personnel (mot de passe provisoire de la mise en service).", statusCode: StatusCodes.Status403Forbidden);

    // Super admin : seulement la restauration et la reprise de données, jamais les écrans des employés
    private static IResult SuperAdminRefuse() => Results.Json(
        new Pharma.Shared.ErreurDto("Le compte super admin ne donne accès qu’à la restauration et à la reprise de données."), statusCode: StatusCodes.Status403Forbidden);

    /// <summary>
    /// Route ouverte à tout employé connecté (pas de droit particulier). Par défaut, le jeton d'un mot de passe provisoire et
    /// celui du super admin sont refusés, comme sur les routes à droit ; motDePasseProvisoire / superAdmin ouvrent la route
    /// à ces jetons pour ce qui leur reste permis (changer son mot de passe, thème, état du poste, ses propres droits).
    /// </summary>
    public static TBuilder ExigerConnexion<TBuilder>(this TBuilder builder, bool motDePasseProvisoire = false, bool superAdmin = false)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out _)) return Results.Unauthorized();
            if (!motDePasseProvisoire && http.User.MotDePasseAChanger()) return MotDePasseAChanger();
            if (!superAdmin && http.User.EstSuperAdmin()) return SuperAdminRefuse();
            return await next(ctx);
        });
        return builder;
    }

    public static TBuilder ExigerTitulaire<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var utilisateurId))
                return Results.Unauthorized();
            if (http.User.MotDePasseAChanger()) return MotDePasseAChanger();
            if (http.User.EstSuperAdmin()) return SuperAdminRefuse();
            var droits = http.RequestServices.GetRequiredService<IPermissionService>();
            if (!await droits.EstTitulaireAsync(utilisateurId, http.RequestAborted))
                return await RefusAsync(http, PermissionServiceExtensions.MarqueTitulaire,
                    http.User.ParCodeComptoir() ? CodeComptoirSeulement : "Réservé au pharmacien titulaire.");
            return await next(ctx);
        });
        return builder;
    }

    public static TBuilder ExigerPermission<TBuilder>(this TBuilder builder, params string[] permissionsAcceptees)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var idTexte = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!long.TryParse(idTexte, out var utilisateurId))
                return Results.Unauthorized();

            if (http.User.MotDePasseAChanger()) return MotDePasseAChanger();
            if (http.User.EstSuperAdmin()) return SuperAdminRefuse();
            var permissions = http.RequestServices.GetRequiredService<IPermissionService>();
            var autorise = false;
            foreach (var p in permissionsAcceptees)
                if (autorise = await permissions.APermissionAsync(utilisateurId, p, http.RequestAborted)) break;
            if (!autorise)
                return await RefusAsync(http, permissionsAcceptees[0],
                    http.User.ParCodeComptoir() && !permissionsAcceptees.Any(Pharma.Domain.Securite.CodeComptoir.Autorise)
                        ? CodeComptoirSeulement
                        : $"Le droit « {string.Join(" » ou « ", permissionsAcceptees)} » est nécessaire.");

            return await next(ctx);
        });
        return builder;
    }
}
