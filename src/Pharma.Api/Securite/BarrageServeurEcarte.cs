using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Bascule du 2e serveur, lot 3 : quand CE serveur est écarté (un autre serveur est devenu principal), toute requête des postes est
/// refusée, consultation et connexion comprises (décision du titulaire, Q9), sauf l'état de santé (/api/healthcheck). Réponse 503
/// ProblemDetails au code stable « serveur_ecarte » ; le poste y lit l'adresse du serveur retenu pour basculer (lot 4).
/// Corps : { type, title, status: 503, detail, code: "serveur_ecarte", motif: "bascule" | "timelines_egales",
///           serveurRetenu: "adresse telle que réglée dans SecondServeur:AutreServeur" | null (motif timelines_egales : aucun serveur retenu, ne pas basculer),
///           ecarteDepuisUtc: "2026-11-23T10:15:00Z" }.
/// </summary>
public static class BarrageServeurEcarte
{
    public const string Code = "serveur_ecarte";
    public const string MotifBascule = "bascule";
    public const string MotifTimelinesEgales = "timelines_egales";
    public const string RouteSante = "/api/healthcheck";

    /// <summary>Vrai si la requête reste permise quand le serveur est écarté : l'état de santé seulement.</summary>
    public static bool Permise(PathString chemin) => chemin.Equals(RouteSante, StringComparison.OrdinalIgnoreCase);

    public static string Motif(EcartDetecte ecart) => ecart.Indecis ? MotifTimelinesEgales : MotifBascule;

    public static Task Filtrer(HttpContext ctx, RequestDelegate suite)
    {
        if (ctx.RequestServices.GetRequiredService<IServeurEcarteDetecte>().Ecart is not { } ecart || Permise(ctx.Request.Path))
            return suite(ctx);
        return Refuser(ctx, ecart);
    }

    public static Task Refuser(HttpContext ctx, EcartDetecte ecart)
    {
        var detail = ecart.Indecis
            ? "Ce serveur est en lecture seule : deux serveurs écrivent avec la même timeline, le titulaire doit trancher. Aucune opération n'est permise."
            : "Ce serveur est écarté : un autre serveur est devenu le serveur principal. Aucune opération n'est permise ici ; le poste doit basculer sur le serveur retenu.";
        return Results.Problem(
            title: "Serveur écarté", detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable,
            type: "urn:pharmaofficine:erreur:serveur_ecarte",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = Code,
                ["motif"] = Motif(ecart),
                ["serveurRetenu"] = ecart.ServeurRetenu,
                ["ecarteDepuisUtc"] = DateTime.SpecifyKind(ecart.DepuisUtc, DateTimeKind.Utc)
            }).ExecuteAsync(ctx);
    }
}
