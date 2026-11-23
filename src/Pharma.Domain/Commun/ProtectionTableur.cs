namespace Pharma.Domain.Commun;

/// <summary>
/// Protection des exports CSV contre l'injection de formule : un tableur lit comme une formule toute cellule texte qui commence par
/// = + - @, une tabulation ou un retour chariot. Une apostrophe est placée devant (le texte reste lisible, la formule n'est pas exécutée).
/// </summary>
public static class ProtectionTableur
{
    private const string Debuts = "=+-@\t\r";

    public static string Neutraliser(string texte) => texte.Length > 0 && Debuts.Contains(texte[0]) ? "'" + texte : texte;
}
