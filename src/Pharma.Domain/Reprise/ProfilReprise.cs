using System.Globalization;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Reprise;

/// <summary>Séparateur décimal des nombres de l'ancien logiciel : Auto = détection habituelle (import.nombre, « 12.500 » signalé comme ambigu).</summary>
public enum SeparateurDecimal { Auto, Virgule, Point }

/// <summary>
/// Transformations simples d'un profil de reprise, appliquées aux valeurs lues AVANT la table de transit :
/// séparateur décimal des nombres, format des dates, codes de classe réglementaire propres à l'ancien logiciel.
/// </summary>
public sealed record TransformationsReprise(SeparateurDecimal Separateur, string? FormatDate, IReadOnlyDictionary<string, string> CodesClasse)
{
    public static readonly TransformationsReprise Aucune = new(SeparateurDecimal.Auto, null, new Dictionary<string, string>());
    public bool EstVide => Separateur == SeparateurDecimal.Auto && FormatDate is null && CodesClasse.Count == 0;
}

/// <summary>Feuille ou table de l'ancien logiciel pour une cible, et champ de reprise → colonne.</summary>
public sealed record CorrespondanceProfil(CibleReprise Cible, string Table, IReadOnlyDictionary<string, string> Colonnes);

/// <summary>
/// Profil de reprise d'un logiciel concurrent : correspondance de colonnes enregistrée et réutilisable.
/// Statut : a_completer (modèle livré vide, format réel inconnu) ou verifie (établi avec un export réel).
/// </summary>
public sealed record ProfilReprise(long Id, string Logiciel, string Version, string Statut, string? Remarque,
    IReadOnlyList<CorrespondanceProfil> Correspondances, TransformationsReprise Transformations,
    DateTime? ModifieLe = null, string? ModifiePar = null);

public static class ProfilsReprise
{
    public static readonly string[] Statuts = ["a_completer", "verifie"];

    /// <summary>Formats de date proposés (motifs .NET) ; « MM/yyyy » : dernier jour du mois, comme import.date_src.</summary>
    public static readonly string[] FormatsDate =
        ["dd/MM/yyyy", "d/M/yyyy", "dd-MM-yyyy", "dd.MM.yyyy", "dd/MM/yy", "yyyy-MM-dd", "yyyy/MM/dd", "yyyyMMdd", "MM/dd/yyyy", "MM/yyyy",
         "dd/MM/yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss"];

    /// <summary>Classes de Pharma Officine qu'un code de l'ancien logiciel peut désigner (reconnues par import.classe_src).</summary>
    public static readonly string[] Classes = ["stupefiant", "psychotrope", "liste_i", "liste_ii", "libre"];

    public const int CodesClasseMax = 50;

    /// <summary>Contrôle d'un profil avant enregistrement ; renvoie le profil nettoyé (espaces retirés, colonnes vides ôtées).</summary>
    public static ProfilReprise Valider(ProfilReprise p)
    {
        var logiciel = (p.Logiciel ?? "").Trim();
        var version = (p.Version ?? "").Trim();
        if (logiciel.Length == 0) throw new RegleMetierException("Indiquez le nom du logiciel.", "logiciel");
        if (logiciel.Length > 100) throw new RegleMetierException("Nom du logiciel : 100 caractères au plus.", "logiciel");
        if (version.Length > 50) throw new RegleMetierException("Version : 50 caractères au plus.", "version");
        if (!Statuts.Contains(p.Statut)) throw new RegleMetierException("Statut du profil inconnu.", "statut");
        if (p.Remarque is { Length: > 1000 }) throw new RegleMetierException("Remarque : 1 000 caractères au plus.", "remarque");

        var t = p.Transformations;
        if (t.FormatDate is not null && !FormatsDate.Contains(t.FormatDate))
            throw new RegleMetierException("Format de date non proposé : choisissez-en un dans la liste.", "formatDate");
        if (t.CodesClasse.Count > CodesClasseMax) throw new RegleMetierException($"{CodesClasseMax} codes de classe au plus.", "codesClasse");
        var codes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (code, classe) in t.CodesClasse)
        {
            var c = (code ?? "").Trim();
            if (c.Length == 0 || c.Length > 50) throw new RegleMetierException("Code de classe vide ou trop long (50 caractères au plus).", "codesClasse");
            if (!Classes.Contains(classe)) throw new RegleMetierException($"Classe inconnue pour le code « {c} ».", "codesClasse");
            if (!codes.TryAdd(c, classe)) throw new RegleMetierException($"Code de classe « {c} » en double.", "codesClasse");
        }

        var correspondances = new List<CorrespondanceProfil>();
        foreach (var c in p.Correspondances)
        {
            var table = (c.Table ?? "").Trim();
            if (table.Length == 0) continue;   // cible non reprise
            if (table.Length > 200) throw new RegleMetierException("Nom de feuille ou de table : 200 caractères au plus.", "correspondances");
            var champs = DetectionReprise.Champs[c.Cible];
            var colonnes = new Dictionary<string, string>();
            foreach (var (champ, colonne) in c.Colonnes)
            {
                if (string.IsNullOrWhiteSpace(colonne)) continue;
                if (!champs.Any(f => f.Code == champ)) throw new RegleMetierException($"Champ de reprise inconnu : {champ}.", "correspondances");
                if (colonne.Trim().Length > 200) throw new RegleMetierException("Nom de colonne : 200 caractères au plus.", "correspondances");
                colonnes[champ] = colonne.Trim();
            }
            correspondances.Add(new CorrespondanceProfil(c.Cible, table, colonnes));
        }
        if (correspondances.GroupBy(c => c.Cible).Any(g => g.Count() > 1))
            throw new RegleMetierException("Une seule feuille ou table par type de données.", "correspondances");

        return p with
        {
            Logiciel = logiciel, Version = version, Remarque = string.IsNullOrWhiteSpace(p.Remarque) ? null : p.Remarque.Trim(),
            Correspondances = correspondances, Transformations = t with { CodesClasse = codes }
        };
    }

    /// <summary>
    /// Valeur lue transformée selon le profil. Une valeur que le profil ne sait pas lire est rendue telle quelle :
    /// les contrôles de la reprise la signalent ensuite (rien n'est deviné).
    /// </summary>
    public static string? Transformer(ChampCible champ, string? valeur, TransformationsReprise t)
    {
        if (string.IsNullOrWhiteSpace(valeur) || t.EstVide) return valeur;
        var v = valeur.Trim();
        if (champ.Code == "classe" && t.CodesClasse.Count > 0)
            return t.CodesClasse.FirstOrDefault(kv => string.Equals(kv.Key, v, StringComparison.OrdinalIgnoreCase)).Value ?? valeur;
        return champ.Nature switch
        {
            NatureValeur.Nombre when t.Separateur != SeparateurDecimal.Auto => Nombre(v, t.Separateur) ?? valeur,
            NatureValeur.Date when t.FormatDate is { } f => Date(v, f) ?? valeur,
            _ => valeur
        };
    }

    /// <summary>Nombre écrit en notation invariante, jamais sous la forme ambiguë « 12.500 » (import.nombre la refuserait).</summary>
    public static string? Nombre(string v, SeparateurDecimal separateur)
    {
        var s = v.Replace(" ", "").Replace(" ", "").Replace(" ", "")
                 .Replace("DZD", "", StringComparison.OrdinalIgnoreCase).Replace("DA", "", StringComparison.OrdinalIgnoreCase);
        s = separateur == SeparateurDecimal.Virgule ? s.Replace(".", "").Replace(',', '.') : s.Replace(",", "");
        if (!decimal.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)) return null;
        var texte = n.ToString("0.############################", CultureInfo.InvariantCulture);
        // « 1.234 » (trois décimales, une à trois unités) serait lu comme ambigu : un zéro de plus lève l'ambiguïté sans changer la valeur
        if (System.Text.RegularExpressions.Regex.IsMatch(texte, @"^-?\d{1,3}\.\d{3}$")) texte += "0";
        return texte;
    }

    /// <summary>Date au format ISO (aaaa-mm-jj) ; format sans jour : « MM/aaaa » (dernier jour du mois, comme import.date_src).</summary>
    public static string? Date(string v, string format)
    {
        if (!DateTime.TryParseExact(v, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return null;
        return format.Contains('d') ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("MM/yyyy", CultureInfo.InvariantCulture);
    }

    public static string CodeSeparateur(SeparateurDecimal s) => s switch { SeparateurDecimal.Virgule => "virgule", SeparateurDecimal.Point => "point", _ => "auto" };

    public static SeparateurDecimal LireSeparateur(string? code) => code switch
    {
        null or "" or "auto" => SeparateurDecimal.Auto, "virgule" => SeparateurDecimal.Virgule, "point" => SeparateurDecimal.Point,
        _ => throw new RegleMetierException("Séparateur décimal : auto, virgule ou point.", "separateurDecimal")
    };
}
