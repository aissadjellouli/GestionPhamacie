namespace Pharma.Desktop.Services;

/// <summary>
/// Saisie d'un pourcentage de remise au poste (caisse, remises automatiques, remise maximale d'un employé) : entier de 0 à 100.
/// Une saisie négative ou supérieure à 100 est refusée avec un message (la valeur précédente reste), jamais ramenée à 0 en silence.
/// Le serveur revérifie avec les mêmes messages.
/// </summary>
public static class SaisieRemise
{
    public const string MessageNegative = "La remise ne peut pas être négative : saisissez un nombre entier de 0 à 100 %.";
    public const string MessageTropGrande = "La remise est un nombre entier de 0 à 100 %.";

    /// <summary>Attribut onbeforeinput : chiffres et signe « - » (le signe est accepté pour pouvoir être refusé avec un message), le reste est ignoré.</summary>
    public const string Filtre =
        "const t = event.data ?? event.dataTransfer?.getData('text') ?? ''; if (/[^0-9-]/.test(t)) event.preventDefault();";

    /// <summary>
    /// Lit la saisie : valeur retenue (champ vidé : 0, zéros de tête retirés) ou message de refus. Une saisie refusée ne donne pas de valeur.
    /// </summary>
    public static (int? Valeur, string? Erreur) Lire(string? brut)
    {
        var texte = (brut ?? "").Trim();
        if (texte.Contains('-')) return (null, MessageNegative);
        var chiffres = CodeComptoirSaisie.Chiffres(texte).TrimStart('0');
        if (chiffres.Length == 0) return (0, null);
        if (!int.TryParse(chiffres, System.Globalization.CultureInfo.InvariantCulture, out var v) || v > 100) return (null, MessageTropGrande);
        return (v, null);
    }
}
