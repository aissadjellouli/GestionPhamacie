using System.Security.Claims;
using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Vague 2, lot V2-1b : note la présence d'un poste (mémoire et, au plus une fois par minute, base : voir <see cref="PresencePostes"/>). Ne bloque jamais la requête
/// et n'en fait jamais échouer une. Sécurité (revue du lot V2-5) : seul compte un poste AUTHENTIFIÉ, c'est-à-dire une requête portant un jeton valide (healthcheck
/// compris : le poste y joint son jeton quand un employé est connecté) ; le nom est pris dans le claim « poste » du jeton (forme canonique <see cref="NomPoste"/>),
/// JAMAIS dans l'en-tête X-Pharma-Poste, que n'importe quel appelant du réseau peut écrire. À placer après l'authentification.
/// </summary>
public sealed class PresencePostesMiddleware(RequestDelegate suite)
{
    public const string EnTete = "X-Pharma-Poste";

    /// <summary>Poste lié au jeton d'une requête authentifiée (forme canonique), sinon null.</summary>
    public static string? PosteAuthentifie(HttpContext ctx) =>
        ctx.User.Identity?.IsAuthenticated == true ? NomPoste.Clair(ctx.User.FindFirstValue(PorteeConnexion.RevendicationPoste)) : null;

    public Task InvokeAsync(HttpContext ctx)
    {
        if (PosteAuthentifie(ctx) is { } poste)
            ctx.RequestServices.GetRequiredService<PresencePostes>().Noter(poste);
        return suite(ctx);
    }
}
