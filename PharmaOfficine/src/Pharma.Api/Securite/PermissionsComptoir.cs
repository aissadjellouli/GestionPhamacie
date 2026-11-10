using System.Security.Claims;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;
using Pharma.Infrastructure.Securite;

namespace Pharma.Api.Securite;

/// <summary>
/// Connexion par code comptoir : seuls les droits du comptoir restent valables (vente, crédit, délivrance,
/// consultation du stock), et jamais ceux du titulaire. S'applique aux filtres comme aux règles métier.
/// </summary>
public sealed class PermissionsComptoir(PermissionService droits, IHttpContextAccessor http) : IPermissionService
{
    private bool ParCode => http.HttpContext?.User.ParCodeComptoir() == true;
    // Mot de passe provisoire pas encore changé : aucun droit
    private bool MotDePasseAChanger => http.HttpContext?.User.MotDePasseAChanger() == true;

    public async Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default) =>
        !MotDePasseAChanger && (!ParCode || CodeComptoir.Autorise(permission)) && await droits.APermissionAsync(utilisateurId, permission, ct);

    public async Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        if (MotDePasseAChanger) return [];
        var liste = await droits.ListerAsync(utilisateurId, ct);
        return ParCode ? liste.Where(CodeComptoir.Autorise).ToList() : liste;
    }

    public Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default) => droits.RemiseMaxAsync(utilisateurId, ct);

    public async Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default) =>
        !ParCode && !MotDePasseAChanger && await droits.EstTitulaireAsync(utilisateurId, ct);
}

public static class PorteeConnexion
{
    /// <summary>En-tête envoyé par chaque poste : son nom (encodé URL), qui identifie la caisse du tiroir.</summary>
    public const string EnTetePoste = "X-Pharma-Poste";

    public static bool ParCodeComptoir(this ClaimsPrincipal user) =>
        user.FindFirstValue(CodeComptoir.Revendication) == CodeComptoir.Portee;

    public static bool MotDePasseAChanger(this ClaimsPrincipal user) =>
        user.FindFirstValue(RegleMotDePasse.Revendication) == RegleMotDePasse.AChanger;

    public static string? Poste(this HttpRequest requete) =>
        requete.Headers[EnTetePoste].FirstOrDefault() is { Length: > 0 } brut ? Uri.UnescapeDataString(brut) : null;
}
