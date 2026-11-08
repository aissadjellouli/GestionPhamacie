using System.Globalization;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;
using PdfSharp.Fonts;
using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;

namespace Pharma.Infrastructure.Impression;

/// <summary>Documents PDF produits avec PDFsharp-MigraDoc (licence MIT), polices Windows du serveur.</summary>
public sealed class DocumentsPdf : IDocumentsPdf
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    static DocumentsPdf()
    {
        if (OperatingSystem.IsWindows())
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public byte[] RapportCloture(DetailSession s, bool avecEcart, decimal seuilEcart, string? pharmacie)
    {
        var doc = NouveauDocument("Rapport de clôture de caisse", pharmacie);
        var section = doc.LastSection;

        Titre(section, "Rapport de clôture de caisse");
        var infos = section.AddParagraph();
        infos.Format.SpaceAfter = Unit.FromPoint(12);
        infos.AddText($"Employé : {s.Employe}   ·   {s.Poste}\n");
        infos.AddText($"Ouverture : {s.OuverteLe.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr)}   ·   ");
        infos.AddText($"Clôture : {s.FermeeLe?.ToLocalTime().ToString("dd/MM/yyyy HH:mm", Fr) ?? "—"}\n");
        infos.AddText($"Ventes de la session : {s.NombreVentes}, pour {Da(s.TotalVentes)} (tous modes de paiement)");

        SousTitre(section, "Espèces comptées");
        var comptage = Tableau(section, 9.5, 3, 4);
        Entete(comptage, "Coupure", "Nombre", "Montant");
        foreach (var l in s.Comptage)
        {
            var libelle = Coupures.Toutes.FirstOrDefault(c => c.Code == l.Coupure).Libelle ?? l.Coupure;
            Ligne(comptage, libelle, l.Coupure == "autres" ? "—" : l.Nombre.ToString(Fr), Da(l.Montant));
        }
        Ligne(comptage, "Total compté", "", Da(s.Compte ?? 0), gras: true);

        if (avecEcart)
        {
            SousTitre(section, "Résultat");
            var resultat = Tableau(section, 12.5, 4);
            Ligne(resultat, "Fond de caisse", Da(s.FondCaisse));
            Ligne(resultat, "Ventes en espèces", Da(s.VentesEspeces ?? 0));
            Ligne(resultat, "Attendu", Da(s.Attendu ?? 0), gras: true);
            Ligne(resultat, "Compté", Da(s.Compte ?? 0));
            Ligne(resultat, "Écart", Da(s.Ecart ?? 0), gras: true);
            if (s.Ecart is { } e && Math.Abs(e) > seuilEcart)
            {
                var p = section.AddParagraph($"Écart supérieur au seuil de {Da(seuilEcart)} : inscrit au journal des actions sensibles.");
                p.Format.SpaceBefore = Unit.FromPoint(8);
                p.Format.Font.Color = Color.FromRgb(0x91, 0x20, 0x18);
            }
        }
        else
        {
            var p = section.AddParagraph("Comptage aveugle : l’attendu et l’écart sont consultables par le titulaire.");
            p.Format.SpaceBefore = Unit.FromPoint(12);
            p.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        }

        Signatures(section);
        return Rendre(doc);
    }

    // ---- Mise en page commune --------------------------------------------

    private static Document NouveauDocument(string titre, string? pharmacie)
    {
        var doc = new Document();
        doc.Info.Title = titre;
        doc.Info.Author = "Pharma Officine";
        var normal = doc.Styles[StyleNames.Normal]!;
        normal.Font.Name = "Arial";
        normal.Font.Size = 10;
        var section = doc.AddSection();
        section.PageSetup.PageFormat = PageFormat.A4;
        section.PageSetup.TopMargin = section.PageSetup.BottomMargin = Unit.FromCentimeter(2);
        section.PageSetup.LeftMargin = section.PageSetup.RightMargin = Unit.FromCentimeter(2);
        var pied = section.Footers.Primary.AddParagraph($"{(pharmacie is null ? "" : pharmacie + " · ")}Pharma Officine · édité le {DateTime.Now.ToString("dd/MM/yyyy HH:mm", Fr)}");
        pied.Format.Font.Size = 8;
        pied.Format.Font.Color = Color.FromRgb(0x47, 0x54, 0x67);
        return doc;
    }

    private static void Titre(Section s, string texte) => LogoPdf.Titre(s, texte);

    private static void SousTitre(Section s, string texte)
    {
        var p = s.AddParagraph(texte);
        p.Format.Font.Size = 12;
        p.Format.Font.Bold = true;
        p.Format.SpaceBefore = Unit.FromPoint(12);
        p.Format.SpaceAfter = Unit.FromPoint(6);
    }

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

    private static void Ligne(Table t, string a, string b, string? c = null, bool gras = false)
    {
        var row = t.AddRow();
        row.TopPadding = row.BottomPadding = Unit.FromPoint(3);
        row.Format.Font.Bold = gras;
        row.Cells[0].AddParagraph(a);
        row.Cells[1].AddParagraph(b);
        if (c is not null) row.Cells[2].AddParagraph(c);
    }

    private static void Signatures(Section s)
    {
        var p = s.AddParagraph("Signature de l’employé : ____________________        Signature du titulaire : ____________________");
        p.Format.SpaceBefore = Unit.FromCentimeter(2);
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
