using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Point d'accroche du relais (lot V2-5, <see cref="RelaisVeille"/>) : appelé en veille pour une route non permise ; renvoie vrai s'il a répondu lui-même
/// (relais vers le serveur 1). Non enregistré sans 2e serveur déclaré ou sans clé des jetons, ou s'il refuse : la requête reçoit alors le 503 « serveur_en_veille ».
/// </summary>
public interface IRelaisVeille
{
    Task<bool> TenterAsync(HttpContext ctx, CancellationToken ct);
}

/// <summary>
/// Vague 2, lot V2-1 : API en veille (2e serveur, base en lecture seule). Toute requête est refusée en 503 ProblemDetails
/// « serveur_en_veille », sauf : /api/healthcheck, /api/vigie/** (rapports des postes, V2-3) et /api/inter-serveurs/** (signées, V2-2).
/// Ces deux dernières routes n'existent pas encore : l'autorisation ici évite que le barrage les masque quand elles seront branchées.
/// </summary>
public static class BarrageServeurVeille
{
    public const string Code = CodesBasculeVague2.ServeurEnVeille;
    public const string RouteVigie = "/api/vigie";
    public const string RouteInterServeurs = "/api/inter-serveurs";

    public static bool Permise(PathString chemin) =>
        BarrageServeurEcarte.Permise(chemin)
        || chemin.StartsWithSegments(RouteVigie, StringComparison.OrdinalIgnoreCase)
        || chemin.StartsWithSegments(RouteInterServeurs, StringComparison.OrdinalIgnoreCase);

    /// <summary>Rôle publié par le healthcheck : écarté, sinon veille, sinon principal.</summary>
    public static string Role(bool ecarte, bool veille) => ecarte ? RoleServeur.Ecarte : veille ? RoleServeur.Veille : RoleServeur.Principal;

    public static async Task Filtrer(HttpContext ctx, RequestDelegate suite)
    {
        if (!ctx.RequestServices.GetRequiredService<IVeilleServeur>().EstVeille || Permise(ctx.Request.Path))
        {
            await suite(ctx);
            return;
        }
        if (ctx.RequestServices.GetService<IRelaisVeille>() is { } relais && await relais.TenterAsync(ctx, ctx.RequestAborted))
            return;
        await Refuser(ctx);
    }

    public static Task Refuser(HttpContext ctx) =>
        Results.Problem(
            title: "Serveur en veille",
            detail: "Ce serveur est le 2e serveur en veille : sa base est une copie en lecture seule. Aucune opération n'est permise ici ; le poste doit utiliser le serveur principal.",
            statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "urn:pharmaofficine:erreur:" + Code,
            extensions: new Dictionary<string, object?> { ["code"] = Code }).ExecuteAsync(ctx);
}
