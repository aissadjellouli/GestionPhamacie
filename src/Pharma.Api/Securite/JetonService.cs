using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Api.Securite;

/// <summary>Jetons JWT signés avec la clé de configuration Jwt:Cle.</summary>
/// <remarks>Liste des postes autorisés : avec un poste prouvé par la requête de connexion (même nom), le jeton reçoit aussi le claim « poste_id ».</remarks>
public sealed class JetonService(IConfiguration config, IHttpContextAccessor? requeteCourante = null) : IGenerateurJeton
{
    public static SymmetricSecurityKey Cle(IConfiguration config) =>
        new(Encoding.UTF8.GetBytes(config["Jwt:Cle"] ?? throw new InvalidOperationException("Jwt:Cle manquante")));

    public const string PrefixeCleExemple = "A-REMPLACER";
    public const int LongueurMinCle = 64;

    /// <summary>
    /// Hors poste de développement, l'API refuse de démarrer avec la clé d'exemple d'appsettings.json (connue de tous :
    /// n'importe qui sur le réseau pourrait signer un jeton de titulaire) ou une clé trop courte.
    /// </summary>
    public static void VerifierCle(IConfiguration config, bool developpement)
    {
        if (developpement) return;
        var cle = config["Jwt:Cle"];
        if (string.IsNullOrWhiteSpace(cle) || cle.StartsWith(PrefixeCleExemple, StringComparison.OrdinalIgnoreCase) || cle.Length < LongueurMinCle)
            throw new InvalidOperationException(
                $"Jwt:Cle absente, trop courte ou restée à la valeur d'exemple : définissez la variable d'environnement Jwt__Cle "
                + $"avec une clé aléatoire d'au moins {LongueurMinCle} caractères, propre à la pharmacie (docs/mise-en-production.md).");
    }

    /// <summary>
    /// Poste de développement : la clé d'exemple reste tolérée (l'API démarre avec les réglages par défaut, qui écoutent sur toutes les
    /// interfaces), mais si l'API est joignable depuis le réseau, n'importe qui peut signer un jeton de titulaire : message d'avertissement
    /// à journaliser au démarrage. Null : rien à signaler.
    /// </summary>
    public static string? AvertissementCleExemple(IConfiguration config, bool developpement)
    {
        if (!developpement) return null;
        var cle = config["Jwt:Cle"];
        if (!string.IsNullOrWhiteSpace(cle) && !cle.StartsWith(PrefixeCleExemple, StringComparison.OrdinalIgnoreCase) && cle.Length >= LongueurMinCle) return null;
        return ReseauSecurise.EcouteAilleursQueLocalhost(config)
            ? "Jwt:Cle absente, trop courte ou restée à la valeur d'exemple, et l'API n'écoute pas seulement sur localhost : toute personne qui joint cette API "
              + "peut fabriquer un jeton de titulaire. Définissez la variable d'environnement Jwt__Cle (64 caractères aléatoires au moins) ou limitez l'écoute à localhost."
            : null;
    }

    /// <summary>Durée de la session du super admin : courte, chaque action demande en plus un code.</summary>
    public static readonly TimeSpan DureeSuperAdmin = TimeSpan.FromHours(1);

    /// <summary>Jeton du super admin : aucun employé (identifiant 0), revendication super_admin.</summary>
    public (string Jeton, DateTime ExpireLe) CreerSuperAdmin(string identifiant) =>
        Signer([new(ClaimTypes.NameIdentifier, "0"), new(ClaimTypes.Name, $"Super admin ({identifiant})"), new(SuperAdminSecurite.Revendication, identifiant)],
            DateTime.UtcNow.Add(DureeSuperAdmin));

    public (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet, bool comptoir = false, bool motDePasseAChanger = false,
        string? poste = null)
    {
        var expire = DateTime.UtcNow.AddHours(config.GetValue("Jwt:DureeHeures", 12));
        var revendications = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, utilisateurId.ToString()),
            new(ClaimTypes.Name, nomComplet)
        };
        // Connexion par code comptoir : droits limités au comptoir (PermissionsComptoir)
        if (comptoir) revendications.Add(new Claim(CodeComptoir.Revendication, CodeComptoir.Portee));
        // Mot de passe provisoire : aucun droit tant qu'il n'est pas changé (PermissionsComptoir, PermissionFilter)
        if (motDePasseAChanger) revendications.Add(new Claim(RegleMotDePasse.Revendication, RegleMotDePasse.AChanger));
        // Titulaire sur un PC non autorisé (ou jeton déjà marqué) : Administration seulement, aucun poste lié (ce PC n'est pas un poste)
        if (AccesAdministrationSeule.Demande(requeteCourante?.HttpContext))
            revendications.Add(new Claim(AccesAdministrationSeule.Revendication, AccesAdministrationSeule.Valeur));
        // Poste de la connexion : lié au jeton (PorteeConnexion.Poste, PosteDuJeton)
        else if (!string.IsNullOrWhiteSpace(poste))
        {
            revendications.Add(new Claim(PorteeConnexion.RevendicationPoste, poste.Trim()));
            // Poste prouvé (signature de la requête de connexion) : identifiant lié au jeton, exigé à chaque requête (PreuvePosteMiddleware)
            if (requeteCourante?.HttpContext?.PosteProuve() is { } prouve && string.Equals(prouve.Nom, poste.Trim(), StringComparison.Ordinal))
                revendications.Add(new Claim(PorteeConnexion.RevendicationPosteId, prouve.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return Signer(revendications, expire);
    }

    private (string Jeton, DateTime ExpireLe) Signer(List<Claim> revendications, DateTime expire)
    {
        var descripteur = new SecurityTokenDescriptor
        {
            Issuer = config["Jwt:Emetteur"],
            Audience = config["Jwt:Emetteur"],
            Expires = expire,
            Subject = new ClaimsIdentity(revendications),
            SigningCredentials = new SigningCredentials(Cle(config), SecurityAlgorithms.HmacSha256)
        };
        return (new JsonWebTokenHandler().CreateToken(descripteur), expire);
    }
}
