using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Notifications;

public enum CanalNotification { Email, Sms }

/// <summary>Sujets auxquels un destinataire s'abonne ; Test sert à vérifier une adresse.</summary>
public enum TypeNotification { RapportQuotidien, EcartCaisse, LotBloque, Sauvegarde, EcartInventaire, Test, LotDelaisse, Surveillance, Synchronisation }

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
        TypeNotification.LotDelaisse => "lot_delaisse",
        TypeNotification.Surveillance => "surveillance",
        TypeNotification.Synchronisation => "synchronisation",
        _ => "test"
    };

    public static TypeNotification TypeDepuisCode(string code) =>
        Enum.GetValues<TypeNotification>().Where(t => t.Code() == code).Select(t => (TypeNotification?)t).FirstOrDefault()
            ?? throw new ArgumentException($"Type de notification inconnu : {code}", nameof(code));

    /// <summary>Types auxquels un destinataire peut s'abonner (le test n'en fait pas partie).</summary>
    public static readonly TypeNotification[] Abonnables =
        [TypeNotification.RapportQuotidien, TypeNotification.EcartCaisse, TypeNotification.LotBloque, TypeNotification.Sauvegarde, TypeNotification.EcartInventaire,
         TypeNotification.LotDelaisse, TypeNotification.Surveillance, TypeNotification.Synchronisation];
}

/// <summary>Nouvelle tentative après un échec : 1, 5, 15, 60 minutes, puis toutes les heures.</summary>
public static class RegleNouvelleTentative
{
    private static readonly TimeSpan[] Paliers =
        [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];

    /// <summary>Au-delà, le message est abandonné (statut « echec ») : une alerte vieille d'une semaine n'a plus de sens.</summary>
    public static readonly TimeSpan DureeMaximale = TimeSpan.FromDays(7);

    /// <summary>Un envoi encore en échec au bout d'un jour d'essais est signalé au titulaire (les essais continuent jusqu'à DureeMaximale).</summary>
    public static readonly TimeSpan DelaiSignalement = TimeSpan.FromDays(1);

    /// <summary>
    /// Échec à signaler au titulaire : une seule fois par message (<paramref name="dejaSignale"/>), dès un jour d'essais, ou à
    /// l'abandon s'il n'a pas été signalé avant (serveur arrêté plusieurs jours, par exemple).
    /// </summary>
    public static bool ASignaler(DateTime maintenantUtc, DateTime creeLeUtc, bool dejaSignale, bool abandon) =>
        !dejaSignale && (abandon || maintenantUtc - creeLeUtc >= DelaiSignalement);

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

    /// <summary>Numéro de mobile algérien valide (après normalisation).</summary>
    public static bool EstMobile(string? telephone) => telephone is not null && FormatMobile().IsMatch(NormaliserTelephone(telephone));

    public static bool EstEmail(string? email) => email is not null && FormatEmail().IsMatch(email.Trim());

    /// <summary>« 0661234512 » devient « 06•••••512 » : le numéro n'est jamais affiché en entier.</summary>
    public static string MasquerTelephone(string telephone)
    {
        var t = NormaliserTelephone(telephone);
        return t.Length <= 5 ? new string('•', t.Length) : t[..2] + new string('•', t.Length - 5) + t[^3..];
    }

    /// <summary>« titulaire@exemple.dz » devient « t•••@exemple.dz ».</summary>
    public static string MasquerEmail(string email)
    {
        var at = email.IndexOf('@');
        return at <= 1 ? "•••" + email[Math.Max(at, 0)..] : email[0] + "•••" + email[at..];
    }

    public static void Verifier(string? nom, string? email, string? telephone, bool canalEmail, bool canalSms, IReadOnlyCollection<TypeNotification> types)
    {
        if (string.IsNullOrWhiteSpace(nom))
            throw new RegleMetierException("Le nom du destinataire est obligatoire.", "nom_destinataire");
        if (!canalEmail && !canalSms)
            throw new RegleMetierException("Choisissez au moins un canal : e-mail ou SMS.");
        if (canalEmail && (email is null || !FormatEmail().IsMatch(email.Trim())))
            throw new RegleMetierException("Adresse e-mail invalide.", "email");
        if (canalSms && (telephone is null || !FormatMobile().IsMatch(NormaliserTelephone(telephone))))
            throw new RegleMetierException("Numéro de mobile invalide : 05, 06 ou 07 suivi de 8 chiffres, ou +213.", "telephone");
        if (types.Count == 0)
            throw new RegleMetierException("Choisissez au moins un type de message.");
        if (types.Contains(TypeNotification.Test))
            throw new RegleMetierException("Le message de test n'est pas un abonnement.");
    }
}

/// <summary>
/// Mise en forme des SMS : texte court, sans accents (jeu de caractères GSM), une seule page. Le modem envoie en mode
/// texte (AT+CMGF=1), qui ne concatène pas : au-delà de 160 caractères le message serait refusé ou tronqué.
/// </summary>
public static class TexteSms
{
    public const int LongueurMax = 160;   // un seul SMS

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
