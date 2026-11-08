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

    public static TBuilder ExigerPermission<TBuilder>(this TBuilder builder, string permission)
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
            if (!await permissions.APermissionAsync(utilisateurId, permission, http.RequestAborted))
                return Results.Problem(
                    title: "Action non autorisée",
                    detail: $"Le droit « {permission} » est nécessaire. Le code du titulaire peut l'autoriser.",
                    statusCode: StatusCodes.Status403Forbidden);

            return await next(ctx);
        });
        return builder;
    }
}
