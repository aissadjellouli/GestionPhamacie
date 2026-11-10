namespace Pharma.Desktop.Services;

/// <summary>
/// Champs obligatoires des formulaires : après un premier essai de validation (« controle »),
/// chaque champ vide passe en rouge (classe champ-invalide, aria-invalid) jusqu'à ce qu'il soit rempli.
/// Le libellé d'un champ obligatoire porte la classe « requis » (astérisque rouge).
/// </summary>
public static class Obligatoire
{
    public static bool Manque(bool controle, string? valeur) => controle && string.IsNullOrWhiteSpace(valeur);
    public static bool Manque<T>(bool controle, T? valeur) where T : struct => controle && valeur is null;
    public static bool Manque(bool controle, long id) => controle && id == 0;

    public static string? Classe(bool invalide) => invalide ? "champ-invalide" : null;
    public static string Aria(bool invalide) => invalide ? "true" : "false";
}
