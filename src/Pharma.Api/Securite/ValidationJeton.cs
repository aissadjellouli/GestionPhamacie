using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Pharma.Application.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// À chaque appel authentifié, l'état du compte est relu en base : compte désactivé, mot de passe changé ou réinitialisé,
/// code comptoir effacé ou base restaurée depuis l'ouverture de la connexion → refus (401), l'employé se reconnecte.
/// Le super admin n'est pas en base (fichier du PC serveur, jeton d'une heure) : non concerné.
/// </summary>
public static class ValidationJeton
{
    public static async Task VerifierAsync(TokenValidatedContext ctx)
    {
        var user = ctx.Principal;
        if (user is null || user.EstSuperAdmin()) return;
        if (!long.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id))
        {
            ctx.Fail("Jeton sans employé.");
            return;
        }
        EtatConnexion? etat;
        try { etat = await ctx.HttpContext.RequestServices.GetRequiredService<IEtatConnexions>().LireAsync(id, ctx.HttpContext.RequestAborted); }
        // Base illisible : le healthcheck (route publique, sans base) répond comme avant ; le jeton est simplement tenu pour non valide (poste non compté « vu »)
        catch (Exception e) when (e is not OperationCanceledException && ctx.HttpContext.Request.Path.StartsWithSegments("/api/healthcheck", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Fail("État du compte illisible.");
            return;
        }
        if (ValidationConnexion.Refus(etat, ctx.SecurityToken.ValidFrom, user.MotDePasseAChanger()) is { } refus)
            ctx.Fail(refus);
    }
}
