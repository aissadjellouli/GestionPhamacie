using System.Data.Odbc;
using System.Text;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>
/// Exports CSV de l'ancien logiciel, lus par Tracodex Officine sans pilote ODBC : chaque valeur est lue comme du texte
/// (le pilote texte de Windows devine le type d'une colonne d'après ses premières lignes et vide les valeurs qui ne
/// correspondent pas), l'encodage (UTF-8 ou ANSI Windows-1252) et le séparateur (virgule, point-virgule, tabulation)
/// sont détectés fichier par fichier. Les fichiers ne sont jamais modifiés.
/// </summary>
internal static class LecteurCsv
{
    /// <summary>Nom du « pilote » proposé à l'écran de reprise pour un dossier de fichiers CSV.</summary>
    public const string Moteur = "Lecteur CSV intégré (Pharma Officine)";

    private static readonly string[] Extensions = [".csv", ".txt", ".tab", ".asc"];
    private const int OctetsDetection = 4 * 1024 * 1024;

    static LecteurCsv() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>Encodage : auto (détecté), utf-8 ou windows-1252 (clé « Encodage » de la chaîne de connexion).</summary>
    public sealed record Options(string Dossier, string Encodage);

    /// <summary>
    /// Chaîne d'un dossier CSV : ce lecteur, ou le pilote texte de Windows (remplacé par ce lecteur pour les raisons ci-dessus).
    /// </summary>
    public static bool EstCsv(string chaine, out Options options)
    {
        options = new Options("", "auto");
        OdbcConnectionStringBuilder b;
        try { b = new OdbcConnectionStringBuilder(chaine); }
        catch (ArgumentException) { return false; }
        var pilote = (b.Driver ?? "").Trim().TrimStart('{').TrimEnd('}');   // le constructeur garde les accolades
        if (!pilote.Equals(Moteur, StringComparison.OrdinalIgnoreCase) && !pilote.Contains("Text Driver", StringComparison.OrdinalIgnoreCase)) return false;
        string? Valeur(string cle) => b.TryGetValue(cle, out var v) ? v?.ToString() : null;
        var dossier = Valeur("Dbq") ?? Valeur("DefaultDir");
        if (string.IsNullOrWhiteSpace(dossier)) throw new RegleMetierException("Indiquez le dossier des fichiers CSV.");
        var encodage = (Valeur("Encodage") ?? "auto").Trim().ToLowerInvariant();
        if (encodage is not ("auto" or "utf-8" or "windows-1252"))
            throw new RegleMetierException("Encodage des fichiers CSV : auto, utf-8 ou windows-1252.");
        options = new Options(dossier.Trim(), encodage);
        return true;
    }

    public static InfoSource Info(Options o)
    {
        var fichiers = Fichiers(o);
        var formats = fichiers.Select(f => Ouvrir(f, o.Encodage)).Select(f => $"{f.NomEncodage}, séparateur {NomSeparateur(f.Separateur)}").Distinct().ToList();
        return new InfoSource(Moteur, string.Join(" ; ", formats), fichiers.Count);
    }

    public static IReadOnlyList<TableSource> Scanner(Options o, int exemples) =>
        Fichiers(o).Select(f =>
        {
            var (entete, lignes) = Lire(f, o.Encodage);
            var echantillon = entete.Select(_ => new List<string?>()).ToArray();
            long n = 0;
            foreach (var l in lignes)
            {
                if (n++ < exemples) for (var i = 0; i < entete.Length; i++) echantillon[i].Add(l[i]);
            }
            return new TableSource(Path.GetFileName(f), n, entete.Select((c, i) => new ColonneSource(c, "Texte", echantillon[i])).ToList());
        }).ToList();

    /// <summary>Colonnes demandées (vérifiées dans l'en-tête du fichier), dans l'ordre demandé.</summary>
    public static IEnumerable<string?[]> Lire(Options o, string table, IReadOnlyList<string> colonnes)
    {
        var fichier = Fichiers(o).FirstOrDefault(f => string.Equals(Path.GetFileName(f), table, StringComparison.OrdinalIgnoreCase))
            ?? throw new RegleMetierException($"Fichier « {table} » introuvable dans le dossier de l’ancienne base.");
        var (entete, lignes) = Lire(fichier, o.Encodage);
        var index = colonnes.Select(c =>
        {
            var i = Array.FindIndex(entete, e => string.Equals(e, c, StringComparison.OrdinalIgnoreCase));
            return i >= 0 ? i : throw new RegleMetierException($"Colonne « {c} » introuvable dans le fichier « {table} ».");
        }).ToArray();
        return lignes.Select(l => index.Select(i => l[i]).ToArray());
    }

    // ---- Fichiers, encodage, séparateur ------------------------------------------------

    private static List<string> Fichiers(Options o)
    {
        if (!Directory.Exists(o.Dossier)) throw new RegleMetierException($"Dossier introuvable sur le PC serveur : {o.Dossier}");
        var fichiers = Directory.EnumerateFiles(o.Dossier)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        if (fichiers.Count == 0) throw new RegleMetierException($"Aucun fichier CSV (.csv, .txt, .tab) dans le dossier {o.Dossier}.");
        return fichiers;
    }

    private sealed record Format(Encoding Encodage, string NomEncodage, char Separateur);

    private static Format Ouvrir(string fichier, string choix)
    {
        byte[] debut;
        using (var flux = File.OpenRead(fichier))
        {
            debut = new byte[(int)Math.Min(flux.Length, OctetsDetection)];
            flux.ReadExactly(debut);
        }
        var (encodage, nom) = Encodage(debut, choix, complet: debut.Length < OctetsDetection);
        var premiere = encodage.GetString(debut).TrimStart('﻿').Split('\n', 2)[0];
        var separateur = Path.GetExtension(fichier).Equals(".tab", StringComparison.OrdinalIgnoreCase) ? '\t' : Separateur(premiere);
        return new Format(encodage, nom, separateur);
    }

    /// <summary>
    /// UTF-8 si le fichier en porte la marque ou s'il est du UTF-8 valide (un texte ANSI accentué ne l'est presque jamais),
    /// sinon Windows-1252 (ANSI des logiciels Windows en français).
    /// </summary>
    internal static (Encoding Encodage, string Nom) Encodage(byte[] debut, string choix, bool complet)
    {
        var ansi = System.Text.Encoding.GetEncoding(1252);
        var utf8 = new UTF8Encoding(false);
        if (choix == "utf-8") return (utf8, "UTF-8 (choisi)");
        if (choix == "windows-1252") return (ansi, "ANSI Windows-1252 (choisi)");
        if (debut is [0xEF, 0xBB, 0xBF, ..]) return (utf8, "UTF-8");
        if (debut is [0xFF, 0xFE, ..]) return (System.Text.Encoding.Unicode, "UTF-16");
        // Fin de l'échantillon : un caractère multi-octets peut y être coupé ; on s'arrête avant lui
        var echantillon = complet ? debut : debut[..FinDeCaractere(debut)];
        try
        {
            new UTF8Encoding(false, true).GetString(echantillon);
            return echantillon.Any(o => o >= 0x80) ? (utf8, "UTF-8") : (utf8, "ASCII");
        }
        catch (DecoderFallbackException) { return (ansi, "ANSI Windows-1252"); }
    }

    /// <summary>Longueur de l'échantillon sans un éventuel caractère UTF-8 incomplet à la fin.</summary>
    internal static int FinDeCaractere(byte[] o)
    {
        var i = o.Length - 1;
        for (var suite = 0; i >= 0 && suite < 3 && (o[i] & 0xC0) == 0x80; suite++) i--;   // octets de continuation
        if (i < 0 || o[i] < 0xC0) return o.Length;                                        // fin sur un caractère complet
        var attendu = o[i] >= 0xF0 ? 4 : o[i] >= 0xE0 ? 3 : 2;
        return o.Length - i < attendu ? i : o.Length;
    }

    /// <summary>Séparateur le plus fréquent de la ligne d'en-tête (hors guillemets) ; virgule par défaut.</summary>
    internal static char Separateur(string entete)
    {
        int virgules = 0, pointsVirgules = 0, tabulations = 0;
        var entreGuillemets = false;
        foreach (var c in entete)
        {
            if (c == '"') entreGuillemets = !entreGuillemets;
            else if (!entreGuillemets)
                switch (c) { case ',': virgules++; break; case ';': pointsVirgules++; break; case '\t': tabulations++; break; }
        }
        return tabulations > virgules && tabulations > pointsVirgules ? '\t' : pointsVirgules > virgules ? ';' : ',';
    }

    private static string NomSeparateur(char c) => c switch { ';' => "point-virgule", '\t' => "tabulation", _ => "virgule" };

    // ---- Lecture ----------------------------------------------------------------------

    private static (string[] Entete, IEnumerable<string?[]> Lignes) Lire(string fichier, string choix)
    {
        var format = Ouvrir(fichier, choix);
        using (var lecteur = new StreamReader(fichier, format.Encodage, detectEncodingFromByteOrderMarks: true))
        {
            var premiere = Enregistrements(lecteur, format.Separateur).FirstOrDefault()
                ?? throw new RegleMetierException($"Fichier « {Path.GetFileName(fichier)} » vide.");
            var entete = NomsDeColonnes(premiere);
            return (entete, Lignes(fichier, format, entete.Length));
        }
    }

    private static IEnumerable<string?[]> Lignes(string fichier, Format format, int colonnes)
    {
        using var lecteur = new StreamReader(fichier, format.Encodage, detectEncodingFromByteOrderMarks: true);
        var premiere = true;
        foreach (var champs in Enregistrements(lecteur, format.Separateur))
        {
            if (premiere) { premiere = false; continue; }
            if (champs.All(string.IsNullOrWhiteSpace)) continue;   // ligne vide
            var ligne = new string?[colonnes];
            for (var i = 0; i < colonnes && i < champs.Count; i++) ligne[i] = string.IsNullOrEmpty(champs[i]) ? null : champs[i];
            yield return ligne;
        }
    }

    /// <summary>En-tête : noms vides ou en double rendus uniques (colonne3, nom_2…), pour que chaque colonne reste choisissable.</summary>
    internal static string[] NomsDeColonnes(IReadOnlyList<string> brut)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return brut.Select((n, i) =>
        {
            var nom = string.IsNullOrWhiteSpace(n) ? $"colonne{i + 1}" : n.Trim().TrimStart('﻿');
            var unique = nom;
            for (var k = 2; !vus.Add(unique); k++) unique = $"{nom}_{k}";
            return unique;
        }).ToArray();
    }

    /// <summary>Enregistrements CSV (RFC 4180) : champs entre guillemets, guillemets doublés, retours à la ligne dans un champ.</summary>
    internal static IEnumerable<List<string>> Enregistrements(TextReader lecteur, char separateur)
    {
        var champs = new List<string>();
        var champ = new StringBuilder();
        bool entreGuillemets = false, contenu = false;
        int c;
        while ((c = lecteur.Read()) >= 0)
        {
            var ch = (char)c;
            if (entreGuillemets)
            {
                if (ch == '"')
                {
                    if (lecteur.Peek() == '"') { champ.Append('"'); lecteur.Read(); }
                    else entreGuillemets = false;
                }
                else champ.Append(ch);
                continue;
            }
            if (ch == '"' && champ.Length == 0) { entreGuillemets = true; contenu = true; }
            else if (ch == separateur) { champs.Add(champ.ToString()); champ.Clear(); contenu = true; }
            else if (ch == '\r') { /* fin de ligne Windows : traitée au \n */ }
            else if (ch == '\n')
            {
                champs.Add(champ.ToString()); champ.Clear();
                yield return champs;
                (champs, contenu) = ([], false);
            }
            else { champ.Append(ch); contenu = true; }
        }
        if (contenu || champ.Length > 0) { champs.Add(champ.ToString()); yield return champs; }
    }
}
