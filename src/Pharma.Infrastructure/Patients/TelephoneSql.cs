namespace Pharma.Infrastructure.Patients;

/// <summary>
/// Numéro enregistré ramené à ses chiffres, « +213 » ou « 00213 » en tête remplacé par « 0 » : comparé par début au préfixe
/// normalisé de la recherche (Pharma.Domain.Ventes.RechercheTelephone). Même règle à la caisse et dans les crédits clients.
/// </summary>
internal static class TelephoneSql
{
    public static string Normalise(string colonne) =>
        $"regexp_replace(regexp_replace(btrim(coalesce({colonne}, '')), '^(\\+|00)[^0-9]*213', '0'), '[^0-9]', '', 'g')";
}
