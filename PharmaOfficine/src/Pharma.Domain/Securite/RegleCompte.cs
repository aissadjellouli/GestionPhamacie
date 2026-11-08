using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Securite;

public static partial class RegleCompte
{
    public const int LongueurMinMotDePasse = 8;

    /// <summary>Identifiant de connexion : 3 à 30 caractères, lettres minuscules, chiffres, point, tiret, souligné.</summary>
    public static string NormaliserIdentifiant(string? identifiant)
    {
        var id = identifiant?.Trim().ToLowerInvariant() ?? "";
        if (!Identifiant().IsMatch(id))
            throw new RegleMetierException("Identifiant : 3 à 30 caractères, lettres minuscules sans accent, chiffres, point, tiret ou souligné.");
        return id;
    }

    public static void VerifierMotDePasse(string? motDePasse)
    {
        if (motDePasse is null || motDePasse.Length < LongueurMinMotDePasse)
            throw new RegleMetierException($"Le mot de passe doit contenir au moins {LongueurMinMotDePasse} caractères.");
        if (string.IsNullOrWhiteSpace(motDePasse))
            throw new RegleMetierException("Le mot de passe ne peut pas être fait que d’espaces.");
    }

    [GeneratedRegex("^[a-z0-9._-]{3,30}$")]
    private static partial Regex Identifiant();
}
