using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace Pharma.Infrastructure.Catalogue;

/// <summary>
/// Lecture des cellules d'un classeur .xlsx (format Office Open XML) sans bibliothèque externe :
/// textes partagés, textes en ligne, nombres. Les formules donnent leur dernière valeur calculée.
/// </summary>
public static class LecteurXlsx
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace NsRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace NsPkg = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Feuilles dans l'ordre du classeur : nom et lignes (cellules en texte, null si vide).</summary>
    public static IReadOnlyList<(string Nom, IReadOnlyList<IReadOnlyList<string?>> Lignes)> Lire(Stream contenu)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(contenu, ZipArchiveMode.Read); }
        catch (InvalidDataException) { throw new FormatException("Le fichier n'est pas un classeur Excel (.xlsx)."); }

        using (zip)
        {
            var partages = LireTextesPartages(zip);
            var classeur = Charger(zip, "xl/workbook.xml") ?? throw new FormatException("Classeur Excel incomplet (xl/workbook.xml absent).");
            var liens = (Charger(zip, "xl/_rels/workbook.xml.rels")?.Root?.Elements(NsPkg + "Relationship") ?? [])
                .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

            var feuilles = new List<(string, IReadOnlyList<IReadOnlyList<string?>>)>();
            foreach (var f in classeur.Root!.Element(Ns + "sheets")?.Elements(Ns + "sheet") ?? [])
            {
                var id = (string?)f.Attribute(NsRel + "id");
                if (id is null || !liens.TryGetValue(id, out var cible)) continue;
                var chemin = cible.StartsWith('/') ? cible.TrimStart('/') : "xl/" + cible;
                var feuille = Charger(zip, chemin);
                if (feuille is null) continue;
                feuilles.Add(((string?)f.Attribute("name") ?? "", LireLignes(feuille, partages)));
            }
            return feuilles;
        }
    }

    private static IReadOnlyList<IReadOnlyList<string?>> LireLignes(XDocument feuille, IReadOnlyList<string> partages)
    {
        var lignes = new List<IReadOnlyList<string?>>();
        foreach (var row in feuille.Root!.Element(Ns + "sheetData")?.Elements(Ns + "row") ?? [])
        {
            var cellules = new List<string?>();
            foreach (var c in row.Elements(Ns + "c"))
            {
                var colonne = Colonne((string?)c.Attribute("r"), cellules.Count);
                while (cellules.Count < colonne) cellules.Add(null);
                cellules.Add(Valeur(c, partages));
            }
            lignes.Add(cellules);
        }
        return lignes;
    }

    private static string? Valeur(XElement c, IReadOnlyList<string> partages)
    {
        var type = (string?)c.Attribute("t");
        if (type == "inlineStr")
            return string.Concat(c.Element(Ns + "is")?.Descendants(Ns + "t").Select(t => t.Value) ?? []);
        var v = c.Element(Ns + "v")?.Value;
        if (v is null) return null;
        return type == "s" && int.TryParse(v, out var i) && i >= 0 && i < partages.Count ? partages[i] : v;
    }

    /// <summary>Indice de colonne depuis la référence « AB12 » (0 pour A) ; à défaut, la suivante.</summary>
    private static int Colonne(string? reference, int suivante)
    {
        if (string.IsNullOrEmpty(reference)) return suivante;
        var n = 0;
        foreach (var ch in reference)
        {
            if (ch is < 'A' or > 'Z') break;
            n = n * 26 + (ch - 'A' + 1);
        }
        return n == 0 ? suivante : n - 1;
    }

    private static List<string> LireTextesPartages(ZipArchive zip) =>
        (Charger(zip, "xl/sharedStrings.xml")?.Root?.Elements(Ns + "si") ?? [])
            .Select(si => string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)))
            .ToList();

    private static XDocument? Charger(ZipArchive zip, string chemin)
    {
        var entree = zip.GetEntry(chemin);
        if (entree is null) return null;
        using var flux = entree.Open();
        // Pas de DTD ni d'entités externes dans un classeur
        using var lecteur = XmlReader.Create(flux, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(lecteur);
    }
}
