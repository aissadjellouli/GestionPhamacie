using System.Globalization;
using System.Text;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using Pharma.Application.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Impression;

/// <summary>Bons de commande et de retour (MigraDoc), planche d'étiquettes EAN-13 (PDFsharp) et étiquettes ZPL.</summary>
public sealed class DocumentsAchats : IDocumentsAchats
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Color Gris = Color.FromRgb(0x47, 0x54, 0x67);

    static DocumentsAchats()
    {
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    // ---- Bon de commande ------------------------------------------------------

    public byte[] BonCommande(CommandeDetail c, Fournisseur f, string? pharmacie)
    {
        var (doc, s) = Nouveau($"Bon de commande {c.Entete.Numero}", pharmacie);
        Titre(s, $"Bon de commande {c.Entete.Numero}");
        var infos = s.AddParagraph();
        infos.Format.SpaceAfter = Unit.FromPoint(12);
        if (pharmacie is not null) infos.AddText($"De : {pharmacie}\n");
        infos.AddText($"À : {f.RaisonSociale}{(f.Telephone is null ? "" : "   ·   " + f.Telephone)}\n");
        infos.AddText($"Date : {c.Entete.Date.ToLocalTime().ToString("dd/MM/yyyy", Fr)}");
        if (c.Entete.Statut == "brouillon")
        {
            var p = s.AddParagraph("BROUILLON : bon non émis");
            p.Format.Font.Bold = true;
            p.Format.Font.Color = Color.FromRgb(0x91, 0x20, 0x18);
            p.Format.SpaceAfter = Unit.FromPoint(8);
        }

        var t = Tableau(s, 8.5, 2.2, 3, 3.3);
        Entete(t, "Produit", "Quantité", "Prix HT", "Montant HT");
        foreach (var l in c.Lignes)
            Ligne(t, false, l.Produit, l.QteCommandee.ToString(Fr), Da(l.PrixAchatHt), Da(l.QteCommandee * l.PrixAchatHt));
        Ligne(t, true, "Total HT", c.Lignes.Sum(l => l.QteCommandee).ToString(Fr), "", Da(c.Lignes.Sum(l => l.QteCommandee * l.PrixAchatHt)));

        var note = s.AddParagraph("Prix indicatifs (dernier prix d’achat connu) : le prix facturé fait foi à la réception.");
        note.Format.SpaceBefore = Unit.FromPoint(10);
        note.Format.Font.Size = 8;
        note.Format.Font.Color = Gris;
        return Rendre(doc);
    }

    // ---- Bon de retour --------------------------------------------------------

    public byte[] BonRetour(RetourResume r, string? pharmacie)
    {
        var (doc, s) = Nouveau($"Bon de retour {r.Numero}", pharmacie);
        Titre(s, $"Bon de retour fournisseur {r.Numero}");
        var infos = s.AddParagraph();
        infos.Format.SpaceAfter = Unit.FromPoint(12);
        if (pharmacie is not null) infos.AddText($"De : {pharmacie}\n");
        infos.AddText($"À : {r.Fournisseur}\n");
        infos.AddText($"Motif : {Motif(r.Motif)}\n");
        infos.AddText($"Préparé le {r.CreeLe.ToLocalTime().ToString("dd/MM/yyyy", Fr)}");
        if (r.EnvoyeLe is { } e) infos.AddText($"   ·   envoyé le {e.ToLocalTime().ToString("dd/MM/yyyy", Fr)}");

        var t = Tableau(s, 9, 4, 3);
        Entete(t, "Produit", "Lot", "Boîtes");
        foreach (var l in r.Lignes) Ligne(t, false, l.Produit, l.NumeroLot, l.Quantite.ToString(Fr));
        Ligne(t, true, "Total", "", r.Lignes.Sum(l => l.Quantite).ToString(Fr));

        var p = s.AddParagraph($"Avoir attendu (au prix d’achat) : {Da(r.MontantAttendu)}");
        p.Format.SpaceBefore = Unit.FromPoint(10);
        var sig = s.AddParagraph("Remis par : ____________________        Reçu par le grossiste : ____________________");
        sig.Format.SpaceBefore = Unit.FromCentimeter(2);
        return Rendre(doc);
    }

    public static string Motif(string code) => code switch
    {
        "peremption_proche" => "péremption proche",
        "perime" => "périmé",
        "defaut" => "défaut",
        "erreur_livraison" => "erreur de livraison",
        "rappel_lot" => "rappel de lot",
        _ => code
    };

    // ---- Étiquettes -----------------------------------------------------------

    private const double LargeurMm = 50, HauteurMm = 30, MargeGaucheMm = 5, MargeHautMm = 13.5;
    private const int Colonnes = 4, Rangees = 9;

    /// <summary>Planche A4 de 4 × 9 étiquettes de 50 × 30 mm, contour gris pour la découpe.</summary>
    public byte[] PlancheEtiquettes(IReadOnlyList<EtiquetteBoite> etiquettes)
    {
        using var pdf = new PdfDocument();
        pdf.Info.Title = "Étiquettes internes";
        var nom = new XFont("Arial", 7, XFontStyleEx.Bold);
        var petit = new XFont("Arial", 6);
        var chiffres = new XFont("Arial", 7);
        var prixPolice = new XFont("Arial", 8, XFontStyleEx.Bold);

        for (var debut = 0; debut < etiquettes.Count; debut += Colonnes * Rangees)
        {
            var page = pdf.AddPage();
            page.Width = XUnit.FromMillimeter(210);
            page.Height = XUnit.FromMillimeter(297);
            using var g = XGraphics.FromPdfPage(page);
            foreach (var (e, i) in etiquettes.Skip(debut).Take(Colonnes * Rangees).Select((e, i) => (e, i)))
            {
                var x = Mm(MargeGaucheMm + i % Colonnes * LargeurMm);
                var y = Mm(MargeHautMm + i / Colonnes * HauteurMm);
                g.DrawRectangle(new XPen(XColor.FromArgb(0xD0, 0xD5, 0xDD), 0.3), x, y, Mm(LargeurMm), Mm(HauteurMm));

                var bord = Mm(2);
                g.DrawString(Couper(e.Produit, 34), nom, XBrushes.Black, new XRect(x + bord, y + Mm(1.5), Mm(LargeurMm - 4), Mm(3)), XStringFormats.TopLeft);
                g.DrawString($"Lot {Couper(e.NumeroLot, 14)}  Exp {e.DatePeremption.ToString("MM/yyyy", Fr)}  {Couper(e.Fournisseur, 12)}",
                    petit, XBrushes.Black, new XRect(x + bord, y + Mm(4.8), Mm(LargeurMm - 4), Mm(3)), XStringFormats.TopLeft);
                DessinerEan13(g, e.Code, x + Mm(7), y + Mm(8.5), Mm(0.33), Mm(14), chiffres);
                g.DrawString($"{e.PrixPublic.ToString("N2", Fr)} DA", prixPolice, XBrushes.Black,
                    new XRect(x, y + Mm(HauteurMm - 4.5), Mm(LargeurMm - 2), Mm(3.5)), XStringFormats.TopRight);
                if (e.Emplacement is not null)
                    g.DrawString($"Empl. {e.Emplacement}", petit, XBrushes.Black,
                        new XRect(x + Mm(2), y + Mm(HauteurMm - 4.2), Mm(LargeurMm / 2), Mm(3)), XStringFormats.TopLeft);
            }
        }
        using var flux = new MemoryStream();
        pdf.Save(flux, false);
        return flux.ToArray();
    }

    /// <summary>Une étiquette ZPL par boîte (format de EtiquetteZpl, une copie chacune).</summary>
    public string EtiquettesZpl(IReadOnlyList<EtiquetteBoite> etiquettes)
    {
        var sb = new StringBuilder();
        foreach (var e in etiquettes)
            sb.Append(EtiquetteZpl.Generer(new EtiquetteLot(e.Produit, e.NumeroLot, e.DatePeremption, e.PrixPublic, Couper(e.Fournisseur, 12), e.Code), 1));
        return sb.ToString();
    }

    // Motifs EAN-13 : jeux A (impair), B (pair) et C ; la parité des 6 premiers chiffres code le premier chiffre
    private static readonly string[] JeuA = ["0001101", "0011001", "0010011", "0111101", "0100011", "0110001", "0101111", "0111011", "0110111", "0001011"];
    private static readonly string[] JeuB = ["0100111", "0110011", "0011011", "0100001", "0011101", "0111001", "0000101", "0010001", "0001001", "0010111"];
    private static readonly string[] JeuC = ["1110010", "1100110", "1101100", "1000010", "1011100", "1001110", "1010000", "1000100", "1001000", "1110100"];
    private static readonly string[] Parites = ["AAAAAA", "AABABB", "AABBAB", "AABBBA", "ABAABB", "ABBAAB", "ABBBAA", "ABABAB", "ABABBA", "ABBABA"];

    /// <summary>Suite des 95 modules (1 = barre) d'un EAN-13 valide.</summary>
    public static string ModulesEan13(string code)
    {
        if (!Ean13.EstValide(code)) throw new ArgumentException($"EAN-13 invalide : {code}");
        var parite = Parites[code[0] - '0'];
        var sb = new StringBuilder("101");
        for (var i = 1; i <= 6; i++) sb.Append((parite[i - 1] == 'A' ? JeuA : JeuB)[code[i] - '0']);
        sb.Append("01010");
        for (var i = 7; i <= 12; i++) sb.Append(JeuC[code[i] - '0']);
        return sb.Append("101").ToString();
    }

    private static void DessinerEan13(XGraphics g, string code, double x, double y, double module, double hauteur, XFont police)
    {
        var modules = ModulesEan13(code);
        var texte = Mm(2.6);
        for (var i = 0; i < modules.Length; i++)
        {
            if (modules[i] != '1') continue;
            // Barres de garde (début, milieu, fin) plus longues, comme sur un EAN-13 standard
            var garde = i < 3 || i is >= 45 and < 50 || i >= 92;
            g.DrawRectangle(XBrushes.Black, x + i * module, y, module, hauteur - (garde ? 0 : texte * 0.6));
        }
        var bas = y + hauteur - texte * 0.55;
        g.DrawString(code[..1], police, XBrushes.Black, new XRect(x - Mm(2.4), bas, Mm(2), texte), XStringFormats.TopRight);
        g.DrawString(code[1..7], police, XBrushes.Black, new XRect(x + 3 * module, bas, 42 * module, texte), XStringFormats.TopCenter);
        g.DrawString(code[7..], police, XBrushes.Black, new XRect(x + 50 * module, bas, 42 * module, texte), XStringFormats.TopCenter);
    }

    private static double Mm(double mm) => XUnit.FromMillimeter(mm).Point;

    private static string Couper(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    // ---- Mise en page commune (MigraDoc) ----------------------------------------

    private static (Document, Section) Nouveau(string titre, string? pharmacie)
    {
        var doc = new Document();
        doc.Info.Title = titre;
        doc.Info.Author = "Pharma Officine";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;
        var s = doc.AddSection();
        s.PageSetup.PageFormat = PageFormat.A4;
        s.PageSetup.TopMargin = s.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        s.PageSetup.LeftMargin = s.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var pied = s.Footers.Primary.AddParagraph($"{(pharmacie is null ? "" : pharmacie + " · ")}Pharma Officine · édité le {DateTime.Now.ToString("dd/MM/yyyy HH:mm", Fr)}");
        pied.Format.Font.Size = 8;
        pied.Format.Font.Color = Gris;
        return (doc, s);
    }

    private static void Titre(Section s, string texte) => LogoPdf.Titre(s, texte);

    private static Table Tableau(Section s, params double[] largeursCm)
    {
        var t = s.AddTable();
        t.Borders.Color = Color.FromRgb(0xE4, 0xE7, 0xEC);
        t.Borders.Width = 0.5;
        foreach (var l in largeursCm) t.AddColumn(Unit.FromCentimeter(l));
        for (var i = 1; i < largeursCm.Length; i++) t.Columns[i].Format.Alignment = ParagraphAlignment.Right;
        return t;
    }

    private static void Entete(Table t, params string[] textes)
    {
        var row = t.AddRow();
        row.Shading.Color = Color.FromRgb(0xF9, 0xFA, 0xFB);
        row.Format.Font.Bold = true;
        for (var i = 0; i < textes.Length; i++) row.Cells[i].AddParagraph(textes[i]);
    }

    private static void Ligne(Table t, bool gras, params string[] cellules)
    {
        var row = t.AddRow();
        row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
        row.Format.Font.Bold = gras;
        for (var i = 0; i < cellules.Length; i++) row.Cells[i].AddParagraph(cellules[i]);
    }

    private static string Da(decimal montant) => montant.ToString("N2", Fr) + " DA";

    private static byte[] Rendre(Document doc)
    {
        var renderer = new PdfDocumentRenderer { Document = doc };
        renderer.RenderDocument();
        using var flux = new MemoryStream();
        renderer.PdfDocument.Save(flux, false);
        return flux.ToArray();
    }
}
