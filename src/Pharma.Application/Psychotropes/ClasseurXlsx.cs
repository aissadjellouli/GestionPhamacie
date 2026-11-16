using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;

namespace Pharma.Application.Psychotropes;

/// <summary>
/// Classeur Excel (.xlsx, SpreadsheetML minimal) écrit sans bibliothèque : une ou plusieurs feuilles, cellules texte (chaînes en
/// ligne) ou nombre ; première ligne de chaque feuille en gras. Aucune formule : un texte qui commence par = reste du texte.
/// </summary>
public static class ClasseurXlsx
{
    public sealed record Feuille(string Nom, IReadOnlyList<IReadOnlyList<object?>> Lignes);

    public static byte[] Ecrire(IReadOnlyList<Feuille> feuilles)
    {
        if (feuilles.Count == 0) throw new ArgumentException("Au moins une feuille.", nameof(feuilles));
        using var flux = new MemoryStream();
        using (var zip = new ZipArchive(flux, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Ajouter(string chemin, string contenu)
            {
                var e = zip.CreateEntry(chemin, CompressionLevel.Optimal);
                using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
                w.Write(contenu);
            }
            var types = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>""");
            var classeur = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets>""");
            var liens = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">""");
            var noms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < feuilles.Count; i++)
            {
                var n = i + 1;
                types.Append($"""<Override PartName="/xl/worksheets/sheet{n}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>""");
                var nom = NomFeuille(feuilles[i].Nom, n);
                while (!noms.Add(nom)) nom = NomFeuille($"{nom} {n}", n);
                classeur.Append($"""<sheet name="{X(nom)}" sheetId="{n}" r:id="rId{n}"/>""");
                liens.Append($"""<Relationship Id="rId{n}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{n}.xml"/>""");
                Ajouter($"xl/worksheets/sheet{n}.xml", FeuilleXml(feuilles[i]));
            }
            liens.Append($"""<Relationship Id="rId{feuilles.Count + 1}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/></Relationships>""");
            Ajouter("[Content_Types].xml", types.Append("</Types>").ToString());
            Ajouter("_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Ajouter("xl/workbook.xml", classeur.Append("</sheets></workbook>").ToString());
            Ajouter("xl/_rels/workbook.xml.rels", liens.ToString());
            Ajouter("xl/styles.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><fonts count="2"><font><sz val="11"/><name val="Calibri"/></font><font><b/><sz val="11"/><name val="Calibri"/></font></fonts><fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills><borders count="1"><border/></borders><cellStyleXfs count="1"><xf/></cellStyleXfs><cellXfs count="2"><xf fontId="0"/><xf fontId="1" applyFont="1"/></cellXfs></styleSheet>""");
        }
        return flux.ToArray();
    }

    private static string FeuilleXml(Feuille f)
    {
        var sb = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        for (var l = 0; l < f.Lignes.Count; l++)
        {
            sb.Append($"<row r=\"{l + 1}\">");
            var style = l == 0 ? " s=\"1\"" : "";
            for (var c = 0; c < f.Lignes[l].Count; c++)
            {
                var v = f.Lignes[l][c];
                if (v is null) continue;
                var reference = Colonne(c) + (l + 1).ToString(CultureInfo.InvariantCulture);
                if (v is int or long or decimal or short)
                    sb.Append($"<c r=\"{reference}\"{style}><v>{Convert.ToString(v, CultureInfo.InvariantCulture)}</v></c>");
                else
                {
                    var texte = v switch
                    {
                        DateTime d => d.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")),
                        DateOnly d => d.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("fr-FR")),
                        IFormattable fm => fm.ToString(null, CultureInfo.GetCultureInfo("fr-FR")),
                        _ => v.ToString() ?? ""
                    };
                    sb.Append($"<c r=\"{reference}\" t=\"inlineStr\"{style}><is><t xml:space=\"preserve\">{X(Nettoyer(texte))}</t></is></c>");
                }
            }
            sb.Append("</row>");
        }
        return sb.Append("</sheetData></worksheet>").ToString();
    }

    /// <summary>A, B, … Z, AA, AB…</summary>
    public static string Colonne(int index)
    {
        var s = "";
        for (var n = index + 1; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string NomFeuille(string nom, int n)
    {
        var propre = new string(nom.Where(c => "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim();
        if (propre.Length == 0) propre = $"Feuille {n}";
        return propre.Length > 31 ? propre[..31] : propre;
    }

    /// <summary>Caractères de contrôle interdits en XML retirés (sauf tabulation et retours à la ligne).</summary>
    private static string Nettoyer(string s) => new(s.Where(c => c >= ' ' || c is '\t' or '\n' or '\r').ToArray());

    private static string X(string s) => SecurityElement.Escape(s) ?? "";
}
