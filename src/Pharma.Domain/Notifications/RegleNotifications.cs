using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Notifications;

public enum CanalNotification { Email, Sms }

/// <summary>Sujets auxquels un destinataire s'abonne ; Test sert à vérifier une adresse.</summary>
public enum TypeNotification { RapportQuotidien, EcartCaisse, LotBloque, Sauvegarde, EcartInventaire, Test }

public static class CodesNotification
{
    public static string Code(this CanalNotification c) => c == CanalNotification.Email ? "email" : "sms";

    public static string Code(this TypeNotification t) => t switch
    {
        TypeNotification.RapportQuotidien => "rapport_quotidien",
        TypeNotification.EcartCaisse => "ecart_caisse",
        TypeNotification.LotBloque => "lot_bloque",
        TypeNotification.Sauvegarde => "sauvegarde",
        TypeNotification.EcartInventaire => "ecart_inventaire",
        _ => "test"
    };

    public static TypeNotification TypeDepuisCode(string code) =>
        Enum.GetValues<TypeNotification>().Where(t => t.Code() == code).Select(t => (TypeNotification?)t).FirstOrDefault()
            ?? throw new ArgumentException($"Type de notification inconnu : {code}", nameof(code));

    /// <summary>Types auxquels un destinataire peut s'abonner (le test n'en fait pas partie).</summary>
    public static readonly TypeNotification[] Abonnables =
        [TypeNotification.RapportQuotidien, TypeNotification.EcartCaisse, TypeNotification.LotBloque, TypeNotification.Sauvegarde, TypeNotification.EcartInventaire];
}

/// <summary>Nouvelle tentative après un échec : 1, 5, 15, 60 minutes, puis toutes les heures.</summary>
public static class RegleNouvelleTentative
{
    private static readonly TimeSpan[] Paliers =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>Au-delà, le message est abandonné (statut « echec ») : une alerte vieille d'une semaine n'a plus de sens.</summary>
    public static readonly TimeSpan DureeMaximale = TimeSpan.FromDays(7);

    /// <param name="tentativesEchouees">Nombre d'échecs déjà constatés, celui-ci compris (1 après le premier échec).</param>
    public static TimeSpan Delai(int tentativesEchouees)
    {
        if (tentativesEchouees < 1) throw new ArgumentOutOfRangeException(nameof(tentativesEchouees));
        return tentativesEchouees <= Paliers.Length ? Paliers[tentativesEchouees - 1] : Paliers[^1];
    }

    /// <summary>Date de la prochaine tentative, ou null si le message doit être abandonné.</summary>
    public static DateTime? Prochaine(DateTime maintenantUtc, DateTime creeLeUtc, int tentativesEchouees)
    {
        var prochaine = maintenantUtc + Delai(tentativesEchouees);
        return prochaine - creeLeUtc > DureeMaximale ? null : prochaine;
    }
}

/// <summary>Fiche d'un destinataire ; seules les adresses vérifiées reçoivent les messages ordinaires.</summary>
public sealed record Destinataire(
    long Id, string Nom, string? Email, string? Telephone, bool CanalEmail, bool CanalSms,
    IReadOnlySet<TypeNotification> Types, bool Valide, bool Actif)
{
    public bool Recoit(TypeNotification type, CanalNotification canal) =>
        Actif
        && (type == TypeNotification.Test || (Valide && Types.Contains(type)))
        && (canal == CanalNotification.Email ? CanalEmail && Email is not null : CanalSms && Telephone is not null);

    public string Adresse(CanalNotification canal) => canal == CanalNotification.Email ? Email! : Telephone!;
}

public static partial class RegleDestinataire
{
    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$")]
    private static partial Regex FormatEmail();

    /// <summary>Numéro algérien : 0 suivi de 9 chiffres (mobile 05, 06, 07), ou +213 suivi de 9 chiffres.</summary>
    [GeneratedRegex(@"^(0[5-7]\d{8}|\+213[5-7]\d{8})$")]
    private static partial Regex FormatMobile();

    public static string NormaliserTelephone(string telephone) =>
        new string(telephone.Where(c => char.IsAsciiDigit(c) || c == '+').ToArray());

    public static void Verifier(string? nom, string? email, string? telephone, bool canalEmail, bool canalSms, IReadOnlyCollection<TypeNotification> types)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new RegleMetierException("Le nom du destinataire est obligatoire.");
        if (!canalEmail && !canalSms)
            throw new RegleMetierException("Choisissez au moins un canal : e-mail ou SMS.");
        if (canalEmail && (email is null || !FormatEmail().IsMatch(email.Trim())))
            throw new RegleMetierException("Adresse e-mail invalide.");
        if (canalSms && (telephone is null || !FormatMobile().IsMatch(NormaliserTelephone(telephone))))
            throw new RegleMetierException("Numéro de mobile invalide : 05, 06 ou 07 suivi de 8 chiffres, ou +213.");
        if (types.Count == 0)
            throw new RegleMetierException("Choisissez au moins un type de message.");
        if (types.Contains(TypeNotification.Test))
            throw new RegleMetierException("Le message de test n'est pas un abonnement.");
    }
}

/// <summary>Mise en forme des SMS : texte court, sans accents (jeu de caractères GSM), une seule page de préférence.</summary>
public static class TexteSms
{
    public const int LongueurMax = 300;   // deux SMS concaténés au plus

    public static string Preparer(string texte)
    {
        var decompose = texte.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decompose.Length);
        foreach (var c in decompose)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(c switch
            {
                '’' or '‘' => '\'',          // apostrophes typographiques
                '«' or '»' => '"',           // guillemets français
                '–' or '—' => '-',           // tirets
                ' ' or ' ' => ' ',           // espaces insécables
                _ => c < 128 ? c : '?'
            });
        }
        var court = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        return court.Length <= LongueurMax ? court : court[..(LongueurMax - 3)] + "...";
    }
}
