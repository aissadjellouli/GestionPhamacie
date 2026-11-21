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

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,59}$")]
    private static partial Regex Identifiant();

    [GeneratedRegex(@"^\+?[0-9]{8,15}$")]
    private static partial Regex Telephone();
}
