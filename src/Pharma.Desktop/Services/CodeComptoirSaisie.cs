namespace Pharma.Desktop.Services;

/// <summary>
/// Saisie du code comptoir : chiffres seulement, vérifiés sur le poste avant l'envoi
/// (mêmes règles que Pharma.Domain.Securite.CodeComptoir, que le poste ne référence pas ; le serveur revérifie).
/// </summary>
public static class CodeComptoirSaisie
{
    /// <summary>Attribut onbeforeinput : refuse tout caractère autre qu'un chiffre, tapé ou collé.</summary>
    public const string FiltreChiffres =
        "const t = event.data ?? event.dataTransfer?.getData('text') ?? ''; if (/[^0-9]/.test(t)) event.preventDefault();";

    public static string Chiffres(string? texte) => new((texte ?? "").Where(char.IsAsciiDigit).ToArray());

    /// <summary>Message à afficher si le code ne convient pas, sinon null.</summary>
    public static string? Erreur(string? code, bool nouveauCode)
    {
        if (string.IsNullOrEmpty(code) || code.Length is < 4 or > 6 || !code.All(char.IsAsciiDigit))
            return "Le code comptoir compte 4 à 6 chiffres.";
        if (!nouveauCode) return null;
        if (code.Distinct().Count() == 1)
            return "Code trop simple : évitez les chiffres tous identiques.";
        var pas = code.Zip(code.Skip(1), (a, b) => b - a).Distinct().ToList();
        if (pas is [1] or [-1])
            return "Code trop simple : évitez les suites (1234, 9876…).";
        return null;
    }
}
