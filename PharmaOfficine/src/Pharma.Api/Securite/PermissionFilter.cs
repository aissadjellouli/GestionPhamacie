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
    // Mot de passe provisoire : tout est refusé tant qu'il n'est pas changé (POST /api/auth/mot-de-passe)
    private static IResult MotDePasseAChanger() => Results.Problem(title: "Mot de passe à changer",
        detail: "Choisissez d’abord votre mot de passe personnel (mot de passe provisoire de la mise en service).", statusCode: StatusCodes.Status403Forbidden);

    public static TBuilder ExigerTitulaire<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var utilisateurId))
                return Results.Unauthorized();
            if (http.User.MotDePasseAChanger()) return MotDePasseAChanger();
            var droits = http.RequestServices.GetRequiredService<IPermissionService>();
            if (!await droits.EstTitulaireAsync(utilisateurId, http.RequestAborted))
                return Results.Problem(title: "Action non autorisée", detail: http.User.ParCodeComptoir() ? CodeComptoirSeulement : "Réservé au pharmacien titulaire.", statusCode: StatusCodes.Status403Forbidden);
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
            var permissions = http.RequestServices.GetRequiredService<IPermissionService>();
            var autorise = false;
            foreach (var p in permissionsAcceptees)
                if (autorise = await permissions.APermissionAsync(utilisateurId, p, http.RequestAborted)) break;
            if (!autorise)
                return http.User.ParCodeComptoir() && !permissionsAcceptees.Any(Pharma.Domain.Securite.CodeComptoir.Autorise)
                    ? Results.Problem(title: "Action non autorisée", detail: CodeComptoirSeulement, statusCode: StatusCodes.Status403Forbidden)
                    : Results.Problem(
                    title: "Action non autorisée",
                    detail: $"Le droit « {string.Join(" » ou « ", permissionsAcceptees)} » est nécessaire. Le code du titulaire peut l'autoriser.",
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(ctx);
        });
        return builder;
    }
}
