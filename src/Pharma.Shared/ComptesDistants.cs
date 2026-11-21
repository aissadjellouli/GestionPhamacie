using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Pharma.Shared;

/// <summary>
/// Comptes distants des écrans du serveur central (lot S7.1, script central 030) : règles communes à la console du central
/// (création, mot de passe provisoire) et aux écrans (changement du mot de passe). Le mot de passe n'est jamais gardé en clair :
/// seule son empreinte BCrypt (coût 12) arrive en base, calculée par l'application.
/// </summary>
public static partial class ComptesDistants
{
    public const int CoutBcrypt = 12;
    public const int LongueurMin = 12;
    public const int LongueurMax = 128;
    public const int LongueurProvisoire = 16;

    private const string AlphabetProvisoire = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Identifiant : minuscules, chiffres, « . _ - », 3 à 60 caractères (même contrôle que la base).</summary>
    public static bool IdentifiantValide(string? identifiant) => identifiant is not null && Identifiant().IsMatch(identifiant);

    public static string? NormaliserIdentifiant(string? identifiant) => identifiant?.Trim().ToLowerInvariant();

    /// <summary>Téléphone : 8 à 15 chiffres, « + » permis en tête.</summary>
    public static bool TelephoneValide(string? telephone) => telephone is not null && Telephone().IsMatch(telephone);

    /// <summary>Mot de passe provisoire tiré au sort (16 caractères, sans caractère ambigu), affiché UNE fois.</summary>
    public static string MotDePasseProvisoire()
    {
        var o = RandomNumberGenerator.GetBytes(LongueurProvisoire);
        return new string(o.Select(b => AlphabetProvisoire[b % AlphabetProvisoire.Length]).ToArray());
    }

    /// <summary>Motif de refus d'un nouveau mot de passe, ou null s'il convient (12 à 128 caractères, lettres et chiffres, différent de l'identifiant).</summary>
    public static string? RefusMotDePasse(string? motDePasse, string identifiant)
    {
        if (string.IsNullOrEmpty(motDePasse) || motDePasse.Length < LongueurMin)
            return $"Le mot de passe doit compter au moins {LongueurMin} caractères.";
        if (motDePasse.Length > LongueurMax) return $"Le mot de passe doit compter au plus {LongueurMax} caractères.";
        if (!motDePasse.Any(char.IsLetter) || !motDePasse.Any(char.IsDigit))
            return "Le mot de passe doit contenir au moins une lettre et un chiffre.";
        if (motDePasse.Contains(identifiant, StringComparison.OrdinalIgnoreCase))
            return "Le mot de passe ne doit pas contenir l'identifiant.";
        if (motDePasse.Distinct().Count() < 5) return "Le mot de passe est trop répétitif.";
        return null;
    }

    // ---- Lot S7.2a (script central 032) : code de rattachement d'une officine à un compte titulaire ----
    public const int LongueurCodeRattachement = 12;

    // 32 caractères sans ambiguïté (ni 0, O, 1, I) : 5 bits par caractère, 60 bits pour 12 caractères ; 256 est multiple de 32 (tirage uniforme)
    private const string AlphabetRattachement = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <summary>Code de rattachement tiré au sort, affiché UNE fois par groupes de 4 (« ABCD-EFGH-JKLM ») ; seule son empreinte va en base.</summary>
    public static string CodeRattachement()
    {
        var o = RandomNumberGenerator.GetBytes(LongueurCodeRattachement);
        var c = new string(o.Select(b => AlphabetRattachement[b % AlphabetRattachement.Length]).ToArray());
        return $"{c[..4]}-{c[4..8]}-{c[8..]}";
    }

    /// <summary>
    /// Code de rattachement et son empreinte, tirés ensemble : seul point de tirage de la console du central (--code-rattachement)
    /// et de la route du poste (POST /api/v1/rattachement, lot S7.2b) ; le code est montré une fois, seule l'empreinte va en base.
    /// </summary>
    public static (string Code, string Empreinte) TirerCodeRattachement()
    {
        var code = CodeRattachement();
        return (code, EmpreinteCodeRattachement(code)!);
    }

    /// <summary>Code saisi ramené à sa forme canonique (majuscules, sans espace ni tiret), ou null s'il ne peut pas être un code.</summary>
    public static string? NormaliserCodeRattachement(string? saisie)
    {
        if (saisie is null || saisie.Length > 40) return null;
        var c = new string(saisie.Where(ch => ch is not (' ' or '-' or '\t')).Select(char.ToUpperInvariant).ToArray());
        return c.Length == LongueurCodeRattachement && c.All(AlphabetRattachement.Contains) ? c : null;
    }

    /// <summary>Empreinte SHA-256 (hexadécimal) du code canonique, préfixée « rattachement-v1: » ; null si la saisie n'est pas un code.</summary>
    public static string? EmpreinteCodeRattachement(string? saisie) =>
        NormaliserCodeRattachement(saisie) is { } c
            ? Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes("rattachement-v1:" + c)))
            : null;

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,59}$")]
    private static partial Regex Identifiant();

    [GeneratedRegex(@"^\+?[0-9]{8,15}$")]
    private static partial Regex Telephone();
}
