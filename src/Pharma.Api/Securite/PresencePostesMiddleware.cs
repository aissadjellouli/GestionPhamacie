using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Vague 2, lot V2-1b : note l'en-tête <c>X-Pharma-Poste</c> de TOUTE requête (healthcheck compris, même refusée ensuite par un barrage) : présence en mémoire
/// et, au plus une fois par minute et par poste, en base (voir <see cref="PresencePostes"/>). Ne bloque jamais la requête et n'en fait jamais échouer une.
/// </summary>
public sealed class PresencePostesMiddleware(RequestDelegate suite)
{
    public const string EnTete = "X-Pharma-Poste";

    public Task InvokeAsync(HttpContext ctx)
    {
        if (ctx.Request.Headers.TryGetValue(EnTete, out var valeur) && valeur.Count == 1)
            ctx.RequestServices.GetRequiredService<PresencePostes>().Noter(valeur[0]);
        return suite(ctx);
    }
}
