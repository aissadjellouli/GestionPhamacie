namespace Pharma.Api.Securite;

/// <summary>
/// Jeton d'employé « sans poste » (décision du titulaire du 2026-11-28) : un client qui ne s'annonce pas comme poste (ni en-tête X-Pharma-Poste
/// ni preuve) n'est pas contrôlé par la liste des postes autorisés. Il s'agit de la page mobile servie par l'API (inventaire, réception,
/// péremptions, emplacements, pilotage du titulaire). Son jeton ne porte aucun poste : il est limité à la liste FERMÉE des routes que cette
/// page appelle. Sans cette limite, un simple outil HTTP du réseau, avec le mot de passe d'un employé, atteignait toutes les routes de ses
/// droits (ventes, patients, finances) depuis un PC jamais autorisé.
/// <para>
/// Non concernés : le jeton d'un poste (lié au poste prouvé), le jeton du super admin (ses routes à lui), le jeton « Administration seulement »
/// du titulaire sur un PC non autorisé (sa propre liste, <see cref="AccesAdministrationSeule"/>), les routes ouvertes.
/// Un test vérifie que chaque appel de wwwroot/mobile/mobile.js est couvert, et que la liste n'ouvre ni la caisse, ni la vente, ni les
/// patients, ni les finances. Toute modification : docs/securite-et-droits.md.
/// </para>
/// </summary>
public static class JetonsSansPoste
{
    /// <summary>Code du refus (ProblemDetails, 403) : la session du client reste ouverte.</summary>
    public const string Code = "poste_requis";

    public const string Message = "Cette fonction n'est disponible que depuis un poste autorisé de la pharmacie.";

    /// <summary>Préfixes permis, toutes méthodes (segments entiers).</summary>
    public static readonly IReadOnlyList<string> Prefixes = ["/api/auth", "/api/mobile", "/api/inventaires"];

    /// <summary>Préfixes permis en lecture seulement (GET).</summary>
    public static readonly IReadOnlyList<string> PrefixesLecture = ["/api/emplacements", "/api/pilotage"];

    public static bool RouteAutorisee(string methode, PathString chemin)
    {
        if (Prefixes.Any(p => chemin.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))) return true;
        if (HttpMethods.IsGet(methode) && PrefixesLecture.Any(p => chemin.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase))) return true;
        // Rangement d'un produit depuis le mobile : PUT /api/produits/{id}/emplacement, rien d'autre de /api/produits
        if (HttpMethods.IsPut(methode) && chemin.Value is { } v)
        {
            var segments = v.Split('/', StringSplitOptions.RemoveEmptyEntries);
            return segments.Length == 4 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase)
                && segments[1].Equals("produits", StringComparison.OrdinalIgnoreCase) && long.TryParse(segments[2], out _)
                && segments[3].Equals("emplacement", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    /// <summary>Jeton d'employé qui ne porte aucun poste (ni nom ni identifiant), hors super admin et hors « Administration seulement ».</summary>
    public static bool Concerne(System.Security.Claims.ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true && !user.EstSuperAdmin() && !AccesAdministrationSeule.JetonMarque(user)
        && user.FindFirst(PorteeConnexion.RevendicationPosteId) is null
        && string.IsNullOrWhiteSpace(user.FindFirst(PorteeConnexion.RevendicationPoste)?.Value);
}
