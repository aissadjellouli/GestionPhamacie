using System.Security.Claims;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;

namespace Pharma.Api.Securite;

/// <summary>
/// Jeton du super admin : revendication « super_admin » (son identifiant), aucun employé derrière (identifiant 0), aucun droit
/// d'employé (PermissionsComptoir). Seules les routes marquées ExigerSuperAdmin l'acceptent.
/// </summary>
public static class SuperAdminSecurite
{
    public const string Revendication = "super_admin";
    /// <summary>Validation par code : « demande:code » (demande renvoyée par POST /api/super-admin/validations).</summary>
    public const string EnTeteValidation = "X-Pharma-Validation";

    public static string? IdentifiantSuperAdmin(this ClaimsPrincipal user) => user.FindFirstValue(Revendication) is { Length: > 0 } s ? s : null;
    public static bool EstSuperAdmin(this ClaimsPrincipal user) => user.IdentifiantSuperAdmin() is not null;

    /// <summary>Réservé au super admin : renseigne SuperAdminCourant pour les règles métier.</summary>
    public static TBuilder ExigerSuperAdmin<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.RequireAuthorization();
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            if (http.User.IdentifiantSuperAdmin() is not { } identifiant)
                return Results.Json(new Pharma.Shared.ErreurDto("Réservé au super admin : connectez-vous avec le compte super admin."), statusCode: StatusCodes.Status403Forbidden);
            http.RequestServices.GetRequiredService<SuperAdminCourant>().Identifiant = identifiant;
            return await next(ctx);
        });
        return builder;
    }

    /// <summary>
    /// Action sensible : le code reçu par SMS (ou e-mail) est vérifié et consommé avant l'action (en-tête X-Pharma-Validation).
    /// Code faux, expiré ou d'une autre action : refus, l'action n'est pas faite.
    /// </summary>
    public static TBuilder ValidationSuperAdmin<TBuilder>(this TBuilder builder, string action) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            var http = ctx.HttpContext;
            var brut = http.Request.Headers[EnTeteValidation].FirstOrDefault() ?? "";
            var parties = brut.Split(':', 2);
            if (parties.Length != 2 || !Guid.TryParse(parties[0], out var demande))
                throw new RegleMetierException("Validation par code manquante : demandez le code, puis saisissez-le.", "code_validation");
            var validation = http.RequestServices.GetRequiredService<ValidationsSuperAdmin>().Verifier(demande, parties[1], action);
            http.RequestServices.GetRequiredService<SuperAdminCourant>().Validation = validation;
            http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.SuperAdmin").LogWarning(
                "Super admin {Identifiant} : action {Action} validée par code ({Canal})",
                http.User.IdentifiantSuperAdmin(), action, validation.Canal);
            return await next(ctx);
        });
        return builder;
    }
}
