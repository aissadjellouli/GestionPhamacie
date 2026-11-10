using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Api.Securite;

/// <summary>Jetons JWT signés avec la clé de configuration Jwt:Cle.</summary>
public sealed class JetonService(IConfiguration config) : IGenerateurJeton
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

    public (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet, bool comptoir = false, bool motDePasseAChanger = false)
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
