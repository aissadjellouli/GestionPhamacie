using Pharma.Domain.Commun;

namespace Pharma.Domain.Securite;

/// <summary>
/// Code personnel de 4 à 6 chiffres pour reprendre la caisse du poste sans la fermer. Plus facile à deviner
/// qu'un mot de passe : il ne donne accès qu'au comptoir, et 5 erreurs le bloquent quelques minutes.
/// </summary>
public static class CodeComptoir
{
    public const int EssaisMax = 5;
    public static readonly TimeSpan Blocage = TimeSpan.FromMinutes(5);

    /// <summary>Nom de la revendication du jeton : connexion par code, limitée au comptoir.</summary>
    public const string Revendication = "portee";
    public const string Portee = "comptoir";

    public static void VerifierFormat(string? code)
    {
        if (code is null || code.Length is < 4 or > 6 || !code.All(char.IsAsciiDigit))
            throw new RegleMetierException("Le code comptoir doit compter 4 à 6 chiffres.");
        if (code.Distinct().Count() == 1)
            throw new RegleMetierException("Code trop simple : évitez les chiffres tous identiques.");
        var montant = code.Zip(code.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (montant is [1] or [-1])
            throw new RegleMetierException("Code trop simple : évitez les suites (1234, 9876…).");
    }

    /// <summary>Droits utilisables avec une connexion par code : vente, crédit, délivrance, consultation du stock.</summary>
    public static bool Autorise(string permission) =>
        permission.StartsWith("caisse.", StringComparison.Ordinal)
        || permission.StartsWith("credit.", StringComparison.Ordinal)
        || permission is Permissions.PsychotropesDelivrer or Permissions.StockConsulter;
}
