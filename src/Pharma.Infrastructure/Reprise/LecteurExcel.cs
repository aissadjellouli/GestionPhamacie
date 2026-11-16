using System.Data.Odbc;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>
/// Classeur Excel (.xlsx) lu par Pharma Officine sans pilote ni bibliothèque extérieure : le fichier est une archive ZIP de XML
/// (format Office Open XML), lue avec System.IO.Compression et System.Xml de .NET, sur le PC serveur, sans aucun envoi.
/// Une feuille = une table ; la première ligne non vide donne les noms de colonnes. Chaque valeur est rendue en texte ;
/// une cellule au format date (nombre de jours Excel) est rendue en aaaa-mm-jj. Le fichier n'est jamais modifié.
/// L'ancien format binaire .xls n'est pas lu : l'enregistrer en .xlsx (ou en CSV) depuis Excel.
/// </summary>
internal static class LecteurExcel
{
    /// <summary>Nom du « pilote » proposé à l'écran de reprise pour un fichier .xlsx.</summary>
    public const string Moteur = "Lecteur Excel intégré (Pharma Officine)";

    private const long TailleMax = 200L * 1024 * 1024;
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace NsPkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    public static bool EstExcel(string chaine, out string fichier)
    {
        fichier = "";
        OdbcConnectionStringBuilder b;
        try { b = new OdbcConnectionStringBuilder(chaine); }
        catch (ArgumentException) { return false; }
        var pilote = (b.Driver ?? "").Trim().TrimStart('{').TrimEnd('}');
        if (!pilote.Equals(Moteur, StringComparison.OrdinalIgnoreCase)) return false;
        var f = b.TryGetValue("Dbq", out var v) ? v?.ToString() : null;
        if (string.IsNullOrWhiteSpace(f)) throw new RegleMetierException("Indiquez le fichier Excel (.xlsx).");
        fichier = f.Trim();
        return true;
    }

    public static InfoSource Info(string fichier)
    {
        using var zip = Ouvrir(fichier);
        return new InfoSource(Moteur, "Classeur .xlsx", Feuilles(zip).Count);
    }

    public static IReadOnlyList<TableSource> Scanner(string fichier, int exemples)
    {
        using var zip = Ouvrir(fichier);
        var partages = Partagees(zip);
        var dates = StylesDate(zip);
        var resultat = new List<TableSource>();
        foreach (var (nom, chemin) in Feuilles(zip))
        {
            string[]? entete = null;
            List<string?>[] echantillon = [];
            long n = 0;
            foreach (var l in Lignes(zip, chemin, partages, dates))
            {
                if (entete is null)
                {
                    if (l.All(string.IsNullOrWhiteSpace)) continue;
                    entete = LecteurCsv.NomsDeColonnes(l.Select(v => v ?? "").ToList());
                    echantillon = entete.Select(_ => new List<string?>()).ToArray();
                    continue;
                }
                if (l.All(string.IsNullOrWhiteSpace)) continue;
                if (n++ < exemples) for (var i = 0; i < entete.Length; i++) echantillon[i].Add(i < l.Count ? l[i] : null);
            }
            if (entete is null) continue;   // feuille vide
            resultat.Add(new TableSource(nom, n, entete.Select((c, i) => new ColonneSource(c, "Texte", echantillon[i])).ToList()));
        }
        return resultat;
    }

    /// <summary>Colonnes demandées (vérifiées dans l'en-tête de la feuille), dans l'ordre demandé.</summary>
    public static IEnumerable<string?[]> Lire(string fichier, string feuille, IReadOnlyList<string> colonnes)
    {
        using var zip = Ouvrir(fichier);
        var (_, chemin) = Feuilles(zip).FirstOrDefault(f => string.Equals(f.Nom, feuille, StringComparison.OrdinalIgnoreCase));
        if (chemin is null) throw new RegleMetierException($"Feuille « {feuille} » introuvable dans le classeur.");
        var partages = Partagees(zip);
        var dates = StylesDate(zip);
        int[]? index = null;
        foreach (var l in Lignes(zip, chemin, partages, dates))
        {
            if (l.All(string.IsNullOrWhiteSpace)) continue;
            if (index is null)
            {
                var entete = LecteurCsv.NomsDeColonnes(l.Select(v => v ?? "").ToList());
                index = colonnes.Select(c =>
                {
                    var i = Array.FindIndex(entete, e => string.Equals(e, c, StringComparison.OrdinalIgnoreCase));
                    return i >= 0 ? i : throw new RegleMetierException($"Colonne « {c} » introuvable dans la feuille « {feuille} ».");
                }).ToArray();
                continue;
            }
            yield return index.Select(i => i < l.Count ? l[i] : null).ToArray();
        }
    }

    // ---- Lecture du format Office Open XML -------------------------------------------------

    private static ZipArchive Ouvrir(string fichier)
    {
        if (!File.Exists(fichier)) throw new RegleMetierException($"Fichier introuvable sur le PC serveur : {fichier}");
        if (Path.GetExtension(fichier).Equals(".xls", StringComparison.OrdinalIgnoreCase))
            throw new RegleMetierException("Ancien format Excel (.xls) : enregistrez le classeur en .xlsx (ou en CSV) depuis Excel.");
        if (new FileInfo(fichier).Length > TailleMax) throw new RegleMetierException("Classeur trop volumineux (200 Mo au plus) : exportez-le en CSV.");
        try
        {
            var flux = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return new ZipArchive(flux, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            throw new RegleMetierException("Ce fichier n’est pas un classeur .xlsx lisible (fichier protégé par mot de passe ou endommagé ?).");
        }
    }

    private static XDocument Xml(ZipArchive zip, string chemin)
    {
        var e = zip.GetEntry(chemin) ?? throw new RegleMetierException($"Classeur incomplet : {chemin} absent.");
        using var s = e.Open();
        // Pas de DTD ni d'entité externe
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(r);
    }

    /// <summary>Feuilles dans l'ordre du classeur : nom affiché et chemin de la partie XML.</summary>
    internal static List<(string Nom, string Chemin)> Feuilles(ZipArchive zip)
    {
        var classeur = Xml(zip, "xl/workbook.xml");
        var liens = zip.GetEntry("xl/_rels/workbook.xml.rels") is null
            ? new Dictionary<string, string>()
            : Xml(zip, "xl/_rels/workbook.xml.rels").Root!.Elements(NsPkg + "Relationship")
                .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);
        var feuilles = new List<(string, string)>();
        foreach (var f in classeur.Root!.Element(Ns + "sheets")?.Elements(Ns + "sheet") ?? [])
        {
            var id = (string?)f.Attribute(NsRel + "id");
            if (id is null || !liens.TryGetValue(id, out var cible)) continue;
            var chemin = cible.StartsWith('/') ? cible.TrimStart('/') : "xl/" + cible;
            if (zip.GetEntry(chemin) is null) continue;   // feuille graphique ou partie absente
            feuilles.Add(((string?)f.Attribute("name") ?? chemin, chemin));
        }
        return feuilles;
    }

    private static List<string> Partagees(ZipArchive zip)
    {
        if (zip.GetEntry("xl/sharedStrings.xml") is null) return [];
        return Xml(zip, "xl/sharedStrings.xml").Root!.Elements(Ns + "si").Select(Texte).ToList();
    }

    /// <summary>Texte d'une chaîne (simple ou enrichie : morceaux r/t), sans la prononciation phonétique.</summary>
    private static string Texte(XElement si) =>
        string.Concat(si.Descendants(Ns + "t").Where(t => t.Parent?.Name != Ns + "rPh").Select(t => t.Value));

    /// <summary>Index des styles de cellule dont le format de nombre est une date.</summary>
    internal static HashSet<int> StylesDate(ZipArchive zip)
    {
        var resultat = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is null) return resultat;
        var styles = Xml(zip, "xl/styles.xml").Root!;
        var formats = styles.Element(Ns + "numFmts")?.Elements(Ns + "numFmt")
            .ToDictionary(f => (int)f.Attribute("numFmtId")!, f => (string?)f.Attribute("formatCode") ?? "") ?? [];
        var i = 0;
        foreach (var xf in styles.Element(Ns + "cellXfs")?.Elements(Ns + "xf") ?? [])
        {
            var id = (int?)xf.Attribute("numFmtId") ?? 0;
            if (EstFormatDate(id, formats.GetValueOrDefault(id))) resultat.Add(i);
            i++;
        }
        return resultat;
    }

    /// <summary>Formats intégrés 14 à 22 et 45 à 47, ou format personnalisé contenant j/m/a hors texte entre guillemets et crochets.</summary>
    internal static bool EstFormatDate(int id, string? code)
    {
        if (id is >= 14 and <= 22 or >= 45 and <= 47) return true;
        if (string.IsNullOrEmpty(code)) return false;
        var sansTexte = System.Text.RegularExpressions.Regex.Replace(code, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", "");
        return sansTexte.IndexOfAny(['d', 'D', 'y', 'Y', 'j', 'J', 'a', 'A']) >= 0 && !sansTexte.Contains("General", StringComparison.OrdinalIgnoreCase)
               || (sansTexte.Contains('m') && !sansTexte.Contains('0') && !sansTexte.Contains('#'));
    }

    /// <summary>Lignes de la feuille, lues en flux (grosses feuilles) ; cellules absentes rendues null à leur place.</summary>
    private static IEnumerable<List<string?>> Lignes(ZipArchive zip, string chemin, List<string> partages, HashSet<int> dates)
    {
        var e = zip.GetEntry(chemin)!;
        using var s = e.Open();
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = true });
        r.MoveToContent();
        while (!r.EOF)
        {
            // XNode.ReadFrom laisse le lecteur sur le nœud suivant (souvent la ligne suivante) : pas de Read() en plus
            if (r.NodeType != XmlNodeType.Element || r.LocalName != "row" || r.NamespaceURI != Ns.NamespaceName) { r.Read(); continue; }
            var ligne = (XElement)XNode.ReadFrom(r);
            var valeurs = new List<string?>();
            var suivante = 0;
            foreach (var c in ligne.Elements(Ns + "c"))
            {
                var col = (string?)c.Attribute("r") is { } reference ? Colonne(reference) : suivante;
                while (valeurs.Count < col) valeurs.Add(null);
                valeurs.Add(Valeur(c, partages, dates));
                suivante = col + 1;
            }
            yield return valeurs;
        }
    }

    /// <summary>Index de colonne (0 = A) d'une référence de cellule « AB12 ».</summary>
    internal static int Colonne(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n - 1;
    }

    private static string? Valeur(XElement c, List<string> partages, HashSet<int> dates)
    {
        var type = (string?)c.Attribute("t");
        if (type == "inlineStr") return c.Element(Ns + "is") is { } inl ? Texte(inl) : null;
        var v = c.Element(Ns + "v")?.Value;
        if (v is null) return null;
        switch (type)
        {
            case "s": return int.TryParse(v, out var i) && i >= 0 && i < partages.Count ? partages[i] : null;
            case "b": return v == "1" ? "1" : "0";
            case "e": return null;   // #N/A, #DIV/0! : vide
            case "str": return v;
        }
        var style = (int?)c.Attribute("s") ?? 0;
        if (dates.Contains(style) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var jours))
            return DateExcel(jours);
        return NombreTexte(v);
    }

    /// <summary>Nombre de jours Excel (système 1900) → aaaa-mm-jj (avec l'heure si elle n'est pas minuit).</summary>
    internal static string? DateExcel(double jours)
    {
        if (jours < 1 || jours > 2958465) return null;
        var d = DateTime.FromOADate(jours);
        return d.TimeOfDay == TimeSpan.Zero ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Nombre enregistré par Excel (point décimal, notation scientifique possible) → texte invariant ; jamais « 12.500 »,
    /// que la reprise lirait comme ambigu (zéro ajouté : même valeur).
    /// </summary>
    internal static string NombreTexte(string v)
    {
        if (!decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return v;
        var t = n.ToString("0.############################", CultureInfo.InvariantCulture);
        return System.Text.RegularExpressions.Regex.IsMatch(t, @"^-?\d{1,3}\.\d{3}$") ? t + "0" : t;
    }
}
