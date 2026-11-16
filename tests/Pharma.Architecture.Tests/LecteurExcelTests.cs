using System.IO.Compression;
using System.Text;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Reprise;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lecteur Excel intégré de la reprise (lot 4) : classeur .xlsx lu sans pilote ni bibliothèque extérieure, une feuille = une table,
/// dates Excel rendues en aaaa-mm-jj, nombres jamais sous la forme ambiguë « 12.500 ».
/// </summary>
public sealed class LecteurExcelTests : IDisposable
{
    private const string Pilote = "Lecteur Excel intégré (Pharma Officine)";
    private readonly string dossier = Directory.CreateTempSubdirectory("pharma-xlsx-").FullName;

    public void Dispose() => Directory.Delete(dossier, recursive: true);

    private string Classeur()
    {
        var fichier = Path.Combine(dossier, "export.xlsx");
        using var zip = ZipFile.Open(fichier, ZipArchiveMode.Create);
        void Ecrire(string chemin, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(chemin).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        const string ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        const string rel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        Ecrire("xl/workbook.xml", $"""<workbook xmlns="{ns}" xmlns:r="{rel}"><sheets><sheet name="Produits" sheetId="1" r:id="rId1"/><sheet name="Factures Chifa" sheetId="2" r:id="rId2"/></sheets></workbook>""");
        Ecrire("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="x" Target="worksheets/sheet1.xml"/><Relationship Id="rId2" Type="x" Target="/xl/worksheets/sheet2.xml"/></Relationships>""");
        Ecrire("xl/sharedStrings.xml", $"""<sst xmlns="{ns}"><si><t>CODE</t></si><si><t>DÉSIGNATION</t></si><si><t>PRIX</t></si><si><r><t>DOLI</t></r><r><t>PRANE</t></r></si><si><t>N_FACTURE</t></si><si><t>DATE</t></si></sst>""");
        Ecrire("xl/styles.xml", $"""<styleSheet xmlns="{ns}"><cellXfs count="2"><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs></styleSheet>""");
        // Ligne 3 : cellule B absente (désignation vide) ; prix 12.5 et 1.234 en nombres
        Ecrire("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="{ns}"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c></row>
            <row r="2"><c r="A2"><v>1001</v></c><c r="B2" t="s"><v>3</v></c><c r="C2"><v>12.5</v></c></row>
            <row r="3"><c r="A3" t="inlineStr"><is><t>A2</t></is></c><c r="C3"><v>1.234</v></c></row>
            </sheetData></worksheet>
            """);
        Ecrire("xl/worksheets/sheet2.xml", $"""
            <worksheet xmlns="{ns}"><sheetData>
            <row r="1"><c r="A1" t="s"><v>4</v></c><c r="B1" t="s"><v>5</v></c></row>
            <row r="2"><c r="A2" t="str"><v>F-001</v></c><c r="B2" s="1"><v>45363</v></c></row>
            </sheetData></worksheet>
            """);
        return fichier;
    }

    [Fact]
    public async Task Feuilles_colonnes_et_valeurs_lues_en_texte()
    {
        var source = new SourceOdbc();
        Assert.Contains(Pilote, source.Pilotes());
        var chaine = $"Driver={{{Pilote}}};Dbq={Classeur()};";

        var info = await source.TesterAsync(chaine);
        Assert.Equal(2, info.NombreTables);
        var tables = await source.ScannerAsync(chaine, 8);
        Assert.Equal(["Produits", "Factures Chifa"], tables.Select(t => t.Nom));
        Assert.Equal(["CODE", "DÉSIGNATION", "PRIX"], tables[0].Colonnes.Select(c => c.Nom));
        Assert.Equal(2, tables[0].Lignes);

        var lignes = new List<string?[]>();
        await foreach (var l in source.LireAsync(chaine, "produits", ["PRIX", "CODE", "Désignation"])) lignes.Add(l);
        Assert.Equal(["12.5", "1001", "DOLIPRANE"], lignes[0]);
        Assert.Equal(["1.2340", "A2", null], lignes[1]);

        var factures = new List<string?[]>();
        await foreach (var l in source.LireAsync(chaine, "Factures Chifa", ["N_FACTURE", "DATE"])) factures.Add(l);
        Assert.Equal(["F-001", "2024-03-12"], factures.Single());
    }

    [Fact]
    public async Task Feuille_ou_colonne_absente_et_ancien_format_refuses_clairement()
    {
        var source = new SourceOdbc();
        var chaine = $"Driver={{{Pilote}}};Dbq={Classeur()};";
        await Assert.ThrowsAsync<RegleMetierException>(async () => { await foreach (var _ in source.LireAsync(chaine, "Stock", ["X"])) { } });
        await Assert.ThrowsAsync<RegleMetierException>(async () => { await foreach (var _ in source.LireAsync(chaine, "Produits", ["INCONNUE"])) { } });
        var xls = Path.Combine(dossier, "ancien.xls");
        await File.WriteAllBytesAsync(xls, [1, 2, 3]);
        await Assert.ThrowsAsync<RegleMetierException>(() => source.TesterAsync($"Driver={{{Pilote}}};Dbq={xls};"));
    }

    [Fact]
    public async Task Classeur_trouve_dans_le_dossier_sans_pilote_installe()
    {
        Classeur();
        var sources = await new SourceOdbc().SourcesAsync(dossier);
        var s = Assert.Single(sources, s => s.Pilote == Pilote);
        Assert.NotNull(s.ChaineConnexion);
    }
}
