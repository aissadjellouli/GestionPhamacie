using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Pharma.Cloud.Api.Securite;

/// <summary>Identité d'un appelant vérifiée : officine, serveur, certificat lié, identifiant du jeton.</summary>
public sealed record AppelantSync(Guid Officine, string Serveur, string EmpreinteCertificat, string Jti);

/// <summary>
/// Jetons d'accès des officines (conception § 6.2) : JWT ES256 de 10 minutes signé par le serveur central seul, lié à l'empreinte
/// du certificat client (cnf.x5t#S256) ; portée sync.envoyer. Vérification : algorithme imposé (ES256), signature, iss, aud, exp
/// (tolérance 10 s), lien au certificat présenté. Aucune donnée sensible dans le jeton (il se lit).
/// </summary>
public sealed class JetonsCentral
{
    public const string Emetteur = "pharma-cloud";
    public const string Audience = "pharma-cloud-sync";
    public const string Portee = "sync.envoyer";
    public static readonly TimeSpan Duree = TimeSpan.FromMinutes(10);
    private readonly ECDsaSecurityKey cle;
    private readonly JsonWebTokenHandler gestionnaire = new() { SetDefaultTimesOnTokenCreation = false };

    public JetonsCentral(ECDsa cleSignature)
    {
        cle = new ECDsaSecurityKey(cleSignature)
        {
            KeyId = Convert.ToHexStringLower(SHA256.HashData(cleSignature.ExportSubjectPublicKeyInfo()))[..16]
        };
    }

    /// <summary>Clé de signature des jetons : créée une fois dans le dossier du serveur central, chiffrée par le mot de passe.</summary>
    public static ECDsa ChargerOuCreerCle(string dossier, string motDePasse)
    {
        var chemin = Path.Combine(dossier, AutoriteCertification.FichierCleJetons);
        var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(chemin)) { cle.ImportFromEncryptedPem(File.ReadAllText(chemin), motDePasse); return cle; }
        AutoriteCertification.VerifierHorsDepot(dossier);
        File.WriteAllText(chemin, cle.ExportEncryptedPkcs8PrivateKeyPem(motDePasse, AutoriteCertification.Pbe()));
        return cle;
    }

    public (string Jeton, DateTimeOffset Expire) Emettre(Guid officine, string serveur, string empreinteCertificat, DateTimeOffset maintenant)
    {
        var expire = maintenant.Add(Duree);
        var jeton = gestionnaire.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Emetteur,
            Audience = Audience,
            IssuedAt = maintenant.UtcDateTime,
            NotBefore = maintenant.UtcDateTime,
            Expires = expire.UtcDateTime,
            Claims = new Dictionary<string, object>
            {
                ["sub"] = officine.ToString("D"),
                ["officine"] = officine.ToString("D"),
                ["serveur"] = serveur,
                ["scope"] = Portee,
                ["jti"] = Guid.NewGuid().ToString("N"),
                ["cnf"] = new Dictionary<string, object> { ["x5t#S256"] = Base64UrlEncoder.Encode(Convert.FromHexString(empreinteCertificat)) }
            },
            SigningCredentials = new SigningCredentials(cle, SecurityAlgorithms.EcdsaSha256)
        });
        return (jeton, expire);
    }

    /// <summary>Jeton valide ET lié au certificat présenté ; null sinon.</summary>
    public async Task<AppelantSync?> VerifierAsync(string jeton, string empreinteCertificatPresente, DateTimeOffset maintenant)
    {
        var r = await gestionnaire.ValidateTokenAsync(jeton, new TokenValidationParameters
        {
            ValidIssuer = Emetteur,
            ValidAudience = Audience,
            IssuerSigningKey = cle,
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            ClockSkew = TimeSpan.FromSeconds(10),
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            LifetimeValidator = (nb, exp, _, p) => exp is { } e && e.Add(p.ClockSkew) > maintenant.UtcDateTime && (nb is null || nb.Value - p.ClockSkew <= maintenant.UtcDateTime)
        });
        if (!r.IsValid || r.SecurityToken is not JsonWebToken jwt) return null;
        if (!jwt.TryGetPayloadValue<string>("scope", out var scope) || scope != Portee) return null;
        if (!jwt.TryGetPayloadValue<string>("officine", out var off) || !Guid.TryParse(off, out var officine)) return null;
        jwt.TryGetPayloadValue<string>("serveur", out var serveur);
        // Lien au certificat (cnf.x5t#S256) : lu dans la charge utile décodée
        using var charge = JsonDocument.Parse(Base64UrlEncoder.Decode(jwt.EncodedPayload));
        if (!charge.RootElement.TryGetProperty("cnf", out var cnf) || !cnf.TryGetProperty("x5t#S256", out var x5t)) return null;
        var attendu = Base64UrlEncoder.Encode(Convert.FromHexString(empreinteCertificatPresente));
        if (!CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(x5t.GetString() ?? ""), System.Text.Encoding.ASCII.GetBytes(attendu)))
            return null;
        return new AppelantSync(officine, serveur ?? "", empreinteCertificatPresente, jwt.Id);
    }
}
