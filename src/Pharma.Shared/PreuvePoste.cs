using System.Security.Cryptography;
using System.Text;

namespace Pharma.Shared;

/// <summary>
/// Preuve de poste (liste des postes autorisés, docs/taches/2026-11-24-postes-autorises/contrat-api.md § 1) : constantes et chaînes signées
/// COMMUNES au poste et au serveur. Le poste signe chaque requête avec sa clé privée ECDSA P-256 (non exportable) ; le serveur vérifie avec la
/// clé publique enregistrée à l'enrôlement. Signature au format IEEE P1363 (64 octets), base64url sans remplissage.
/// </summary>
public static class PreuvePoste
{
    public const string EnTeteId = "X-Pharma-Poste-Id";
    public const string EnTeteHorodatage = "X-Pharma-Poste-Horodatage";
    public const string EnTeteNonce = "X-Pharma-Poste-Nonce";
    public const string EnTetePreuve = "X-Pharma-Poste-Preuve";
    /// <summary>Heure du serveur (secondes Unix UTC), posée sur un refus « preuve_horloge » : le poste corrige son décalage.</summary>
    public const string EnTeteHeureServeur = "X-Pharma-Heure-Serveur";

    public const string Version = "PHARMA-POSTE-V1";
    public const string VersionEnrolement = "PHARMA-POSTE-ENROLEMENT-V1";
    public const int FenetreHorlogeSecondes = 120;
    public const int OctetsNonce = 16;
    public const int OctetsSignature = 64;

    // Codes de refus (ProblemDetails : type urn:pharmaofficine:erreur:<code>, extension « code »)
    public const string PosteNonProuve = "poste_non_prouve";
    public const string PosteRevoque = "poste_revoque";
    public const string PreuveHorloge = "preuve_horloge";
    public const string PosteNonAutorise = "poste_non_autorise";
    public const string PreuveIndisponible = "preuve_indisponible";
    public const string CodeInvalide = "code_invalide";
    public const string CleInvalide = "cle_invalide";
    public const string NomPostePris = "nom_poste_pris";
    public const string PosteOccupe = "poste_occupe";

    // Modes (parametre_postes.mode)
    public const string ModeInactif = "inactif";
    public const string ModeTransition = "transition";
    public const string ModeExige = "exige";

    /// <summary>Chaîne signée d'une requête (UTF-8, lignes séparées par « \n », sans « \n » final).</summary>
    public static string Chaine(string methode, string cible, long horodatage, string nonce, long posteId, string? authorization) =>
        string.Join('\n', Version, methode.ToUpperInvariant(), cible, horodatage.ToString(System.Globalization.CultureInfo.InvariantCulture), nonce,
            posteId.ToString(System.Globalization.CultureInfo.InvariantCulture), EmpreinteAutorisation(authorization));

    /// <summary>SHA-256 hexadécimal minuscule de la valeur de l'en-tête Authorization ; vide sans en-tête.</summary>
    public static string EmpreinteAutorisation(string? authorization) =>
        string.IsNullOrEmpty(authorization) ? "" : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(authorization)));

    /// <summary>Code d'enrôlement normalisé : majuscules, sans tiret ni espace.</summary>
    public static string NormaliserCode(string? code) =>
        new((code ?? "").Where(c => !char.IsWhiteSpace(c) && c != '-').Select(char.ToUpperInvariant).ToArray());

    /// <summary>Chaîne signée par la nouvelle clé à l'enrôlement (preuve de possession).</summary>
    public static string ChaineEnrolement(string code, long horodatage, byte[] clePubliqueSpki) =>
        string.Join('\n', VersionEnrolement, NormaliserCode(code), horodatage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexStringLower(SHA256.HashData(clePubliqueSpki)));

    /// <summary>Empreinte courte d'une clé publique (16 caractères hexadécimaux majuscules du SHA-256 de la forme SPKI).</summary>
    public static string EmpreinteCle(byte[] clePubliqueSpki) => Convert.ToHexString(SHA256.HashData(clePubliqueSpki))[..16];

    public static string NouveauNonce() => Base64Url(RandomNumberGenerator.GetBytes(OctetsNonce));

    public static string Base64Url(byte[] octets) => Convert.ToBase64String(octets).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Décodage base64url (sans remplissage) ; null si mal formé.</summary>
    public static byte[]? DepuisBase64Url(string? texte)
    {
        if (string.IsNullOrEmpty(texte) || texte.Length > 4096) return null;
        var s = texte.Replace('-', '+').Replace('_', '/');
        s = (s.Length % 4) switch { 2 => s + "==", 3 => s + "=", 0 => s, _ => null! };
        if (s is null) return null;
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }

    /// <summary>Clé publique SPKI lue si c'est bien une clé ECDSA P-256 ; sinon null.</summary>
    public static ECDsa? ClePublique(byte[] spki)
    {
        var cle = ECDsa.Create();
        try
        {
            cle.ImportSubjectPublicKeyInfo(spki, out var lus);
            var p = cle.ExportParameters(false);
            if (lus != spki.Length || p.Curve.Oid.Value != ECCurve.NamedCurves.nistP256.Oid.Value) { cle.Dispose(); return null; }
            return cle;
        }
        catch (CryptographicException) { cle.Dispose(); return null; }
    }

    /// <summary>Vérifie une signature P1363 (base64url) de <paramref name="chaine"/> avec la clé publique SPKI.</summary>
    public static bool Verifier(byte[] spki, string chaine, string? signature)
    {
        if (DepuisBase64Url(signature) is not { Length: OctetsSignature } sig) return false;
        using var cle = ClePublique(spki);
        return cle is not null && cle.VerifyData(Encoding.UTF8.GetBytes(chaine), sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Signe <paramref name="chaine"/> (P1363, base64url).</summary>
    public static string Signer(ECDsa cle, string chaine) =>
        Base64Url(cle.SignData(Encoding.UTF8.GetBytes(chaine), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
}
