namespace Pharma.Infrastructure.Referentiels;

/// <summary>
/// Saisie cherchée avec <c>ILIKE '%' || … || '%' ESCAPE '\'</c> : \, % et _ tapés sont pris à la lettre (« __ » ou « % » ne renvoient
/// pas tout). La saisie reste un paramètre lié ; sans_accent ne touche pas à ces trois caractères.
/// </summary>
internal static class MotifRecherche
{
    public static string Echapper(string texte) => texte.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    public static string? EchapperOuNull(string? texte) => texte is null ? null : Echapper(texte);
}
