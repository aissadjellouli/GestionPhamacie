using System.Security.Claims;
using Pharma.Application.Securite;

namespace Pharma.Api.Securite;

/// <summary>Refuse l'appel si l'utilisateur n'a pas la permission (vérifiée en base à chaque appel).</summary>
public static class PermissionExtensions
{
    /// <summary>Identifiant de l'employé connecté (garanti par ExigerPermission).</summary>
    public static long UtilisateurId(this ClaimsPrincipal user) =>
        long.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)
                   ?? throw new InvalidOperationException("Utilisateur non authentifié."));

    /// <summary>Une seule des permissions suffit (lecture commune à plusieurs droits).</summary>
    /// <summary>Réservé au pharmacien titulaire (rôle système), quel que soit le réglage des droits.</summary>
    public static TBuilder ExigerTitulaire<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (!long.TryParse(http.User.FindFirstValue(ClaimTypes.NameIdentifier), out var utilisateurId))
                return Results.Unauthorized();
            var droits = http.RequestServices.GetRequiredService<IPermissionService>();
            if (!await droits.EstTitulaireAsync(utilisateurId, http.RequestAborted))
                return Results.Problem(title: "Action non autorisée", detail: "Réservé au pharmacien titulaire.", statusCode: StatusCodes.Status403Forbidden);
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

            var permissions = http.RequestServices.GetRequiredService<IPermissionService>();
            var autorise = false;
            foreach (var p in permissionsAcceptees)
                if (autorise = await permissions.APermissionAsync(utilisateurId, p, http.RequestAborted)) break;
            if (!autorise)
                return Results.Problem(
                    title: "Action non autorisée",
                    detail: $"Le droit « {string.Join(" » ou « ", permissionsAcceptees)} » est nécessaire. Le code du titulaire peut l'autoriser.",
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(ctx);
        });
        return builder;
    }
}
