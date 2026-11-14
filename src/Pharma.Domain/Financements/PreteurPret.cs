using Pharma.Domain.Commun;

namespace Pharma.Domain.Financements;

/// <summary>
/// Qui accorde un prêt : une banque suivie en trésorerie (compte de type banque) ou un autre prêteur, hors trésorerie, dont le nom
/// est saisi librement ; exactement l'un des deux. L'argent d'un prêteur hors trésorerie n'entre dans aucun compte suivi : il ne
/// sert que directement (facture ou traite d'un grossiste, dépense).
/// </summary>
public static class PreteurPret
{
    public const int LongueurMax = 120;

    /// <summary>
    /// Contrôle le choix : banque de la trésorerie (<paramref name="compteId"/>) ou nom libre (<paramref name="preteur"/>), pas les deux,
    /// pas aucun. Renvoie le nom libre nettoyé (espaces en trop retirés), ou null pour une banque de la trésorerie.
    /// </summary>
    public static string? Verifier(long? compteId, string? preteur)
    {
        var nom = Nettoyer(preteur);
        if (compteId is > 0 && nom is not null)
            throw new RegleMetierException("Choisissez une banque suivie en trésorerie ou saisissez le nom d’un autre prêteur, pas les deux.", "preteur");
        if (compteId is not > 0 && nom is null)
            throw new RegleMetierException("Choisissez la banque qui accorde le prêt, ou saisissez le nom du prêteur.", compteId is null ? "preteur" : "compte");
        if (nom is { Length: > LongueurMax })
            throw new RegleMetierException($"Nom du prêteur : {LongueurMax} caractères au plus.", "preteur");
        return nom;
    }

    /// <summary>Clé d'un prêteur libre (colonnes d'import retenues) : sans tenir compte des majuscules ni des espaces en trop.</summary>
    public static string Cle(string preteur) => (Nettoyer(preteur) ?? "").ToLowerInvariant();

    private static string? Nettoyer(string? texte) =>
        string.IsNullOrWhiteSpace(texte) ? null : string.Join(' ', texte.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
