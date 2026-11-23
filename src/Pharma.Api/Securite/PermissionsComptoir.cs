using System.Security.Claims;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;
using Pharma.Infrastructure.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// Connexion par code comptoir : seuls les droits du comptoir restent valables (vente, crédit, délivrance,
/// consultation du stock), et jamais ceux du titulaire. S'applique aux filtres comme aux règles métier.
/// </summary>
public sealed class PermissionsComptoir(PermissionService droits, IHttpContextAccessor http, AutorisationParCode? autorisation = null) : IPermissionService
{
    // Autorisation sur place : seulement pour l'employé de la requête (jamais quand on interroge les droits d'un autre employé)
    private bool EstUtilisateurCourant(long utilisateurId) =>
        long.TryParse(http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) && id == utilisateurId;

    // Par le code d'un collègue, seulement un droit de la liste blanche (CodeComptoir.AutorisablesParCollegue : jamais le titulaire,
    // l'administration, la trésorerie, les financements, la surveillance, ni la délivrance d'un psychotrope) ; sur une connexion par
    // code comptoir, en plus, un droit du comptoir (portée de la connexion conservée).
    public static bool AutorisableSurPlace(ClaimsPrincipal user, string permission) =>
        CodeComptoir.AutorisableParCollegue(permission)
        && (!user.ParCodeComptoir() || CodeComptoir.Autorise(permission) || CodeComptoir.ActionsAutorisables.ContainsKey(permission));

    private async Task<bool> AutoriseSurPlaceAsync(long utilisateurId, string permission, CancellationToken ct) =>
        autorisation is not null && !MotDePasseAChanger && EstUtilisateurCourant(utilisateurId)
        && http.HttpContext?.User is { } u && AutorisableSurPlace(u, permission)
        && await autorisation.AutoriseAsync(utilisateurId, permission, ct);

    private bool ParCode => http.HttpContext?.User.ParCodeComptoir() == true;
    // Mot de passe provisoire pas encore changé, ou super admin (aucun droit d'employé) : aucun droit
    private bool MotDePasseAChanger => http.HttpContext?.User is { } u && (u.MotDePasseAChanger() || u.EstSuperAdmin());

    public async Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default) =>
        (!MotDePasseAChanger && (!ParCode || CodeComptoir.Autorise(permission)) && await droits.APermissionAsync(utilisateurId, permission, ct))
        || await AutoriseSurPlaceAsync(utilisateurId, permission, ct);

    public async Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        if (MotDePasseAChanger) return [];
        var liste = await droits.ListerAsync(utilisateurId, ct);
        return ParCode ? liste.Where(CodeComptoir.Autorise).ToList() : liste;
    }

    public async Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default) =>
        MotDePasseAChanger ? 0 : await droits.RemiseMaxAsync(utilisateurId, ct);

    // Au-delà de son maximum : code d'un collègue dont le maximum couvre ce pourcentage (jamais le sien)
    public async Task<bool> RemiseAutoriseeAsync(long utilisateurId, decimal pourcentage, CancellationToken ct = default) =>
        await RemiseMaxAsync(utilisateurId, ct) >= pourcentage
        || (autorisation is not null && !MotDePasseAChanger && EstUtilisateurCourant(utilisateurId)
            && http.HttpContext?.User is { } u && AutorisableSurPlace(u, Permissions.CaisseRemise)
            && await autorisation.AutoriseAsync(utilisateurId, Permissions.CaisseRemise, ct, remisePct: pourcentage));

    public async Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default) =>
        (!ParCode && !MotDePasseAChanger && await droits.EstTitulaireAsync(utilisateurId, ct))
        || await AutoriseSurPlaceAsync(utilisateurId, PermissionServiceExtensions.MarqueTitulaire, ct);

    // Libellé lu en base : sans cette transmission, la méthode par défaut de l'interface rendait le code (« prix.modifier »)
    public Task<string> LibelleAsync(string permission, CancellationToken ct = default) => droits.LibelleAsync(permission, ct);
}

public static class PorteeConnexion
{
    /// <summary>En-tête envoyé par chaque poste : son nom (encodé URL), qui identifie la caisse du tiroir.</summary>
    public const string EnTetePoste = "X-Pharma-Poste";

    public static bool ParCodeComptoir(this ClaimsPrincipal user) =>
        user.FindFirstValue(CodeComptoir.Revendication) == CodeComptoir.Portee;

    public static bool MotDePasseAChanger(this ClaimsPrincipal user) =>
        user.FindFirstValue(RegleMotDePasse.Revendication) == RegleMotDePasse.AChanger;

    /// <summary>Revendication du jeton : poste sur lequel l'employé s'est connecté.</summary>
    public const string RevendicationPoste = "poste";

    private static string? PosteAnnonce(HttpRequest requete) =>
        requete.Headers[EnTetePoste].FirstOrDefault() is { Length: > 0 } brut && Uri.UnescapeDataString(brut).Trim() is { Length: > 0 } poste ? poste : null;

    /// <summary>
    /// Poste de la requête. Avec une connexion, c'est le poste lié au jeton (jamais l'en-tête, que n'importe quel client peut écrire) ;
    /// un jeton sans poste n'en a aucun. Sans connexion (écran de connexion), c'est l'en-tête.
    /// </summary>
    public static string? Poste(this HttpRequest requete) =>
        requete.HttpContext.User.Identity?.IsAuthenticated == true
            ? (requete.HttpContext.User.FindFirstValue(RevendicationPoste) is { Length: > 0 } lie ? lie : null)
            : PosteAnnonce(requete);

    /// <summary>Vrai si le jeton est lié à un poste et que l'en-tête en annonce un autre.</summary>
    public static bool PosteDiffere(HttpContext http) =>
        http.User.Identity?.IsAuthenticated == true
        && http.User.FindFirstValue(RevendicationPoste) is { Length: > 0 } lie
        && PosteAnnonce(http.Request) is { } annonce && !string.Equals(annonce, lie, StringComparison.Ordinal);
}
