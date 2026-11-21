using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Pharma.Infrastructure.Impression;

/// <summary>
/// Filigrane « DÉMONSTRATION » des PDF produits par l'API en mode démonstration (Demo:Actif) : texte gris clair en diagonale sur
/// chaque page, posé au-dessus du contenu, et mention dans les propriétés du document. Inactif (défaut) : PDF inchangés.
/// </summary>
public sealed record FiligranePdf(bool Actif)
{
    public const string Texte = "DÉMONSTRATION";
    public const string Mention = "Démonstration — données fictives";

    /// <summary>Pose le filigrane sur toutes les pages (sans effet si inactif).</summary>
    public void Poser(PdfDocument pdf)
    {
        if (!Actif) return;
        pdf.Info.Subject = Mention;
        pdf.Info.Keywords = Texte;
        foreach (var page in pdf.Pages)
        {
            using var g = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
            var (largeur, hauteur) = (page.Width.Point, page.Height.Point);
            // Taille selon la page : un ticket de 58 mm reçoit un filigrane à sa mesure, une page A4 un grand filigrane
            var taille = Math.Clamp(Math.Min(largeur, hauteur) / 7.5, 8, 72);
            var police = new XFont("Arial", taille, XFontStyleEx.Bold);
            var angle = largeur < hauteur ? -Math.Atan2(hauteur, largeur) * 180 / Math.PI : -30;
            g.TranslateTransform(largeur / 2, hauteur / 2);
            g.RotateTransform(angle);
            g.DrawString(Texte, police, new XSolidBrush(XColor.FromArgb(70, 128, 128, 128)), new XPoint(0, 0), XStringFormats.Center);
        }
    }
}
