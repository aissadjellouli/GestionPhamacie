using MigraDoc.DocumentObjectModel;

namespace Pharma.Infrastructure.Impression;

/// <summary>Logo Tracodex (gélule, icône 192 px), intégré à l'assemblage, placé devant le titre des documents.</summary>
internal static class LogoPdf
{
    private static readonly Lazy<string> Base64 = new(() =>
    {
        using var flux = typeof(LogoPdf).Assembly.GetManifestResourceStream("Pharma.Logo.png")
            ?? throw new InvalidOperationException("Ressource du logo introuvable.");
        using var copie = new MemoryStream();
        flux.CopyTo(copie);
        return "base64:" + Convert.ToBase64String(copie.ToArray());
    });

    /// <summary>Titre précédé du logo, sur une même ligne.</summary>
    public static Paragraph Titre(Section s, string texte)
    {
        var p = s.AddParagraph();
        var image = p.AddImage(Base64.Value);
        image.Height = Unit.FromCentimeter(1);
        image.LockAspectRatio = true;
        p.AddText("  " + texte);
        p.Format.Font.Size = 16;
        p.Format.Font.Bold = true;
        p.Format.Font.Color = Color.FromRgb(0x0C, 0x65, 0x5E);   // vert du texte de la charte (logo Tracodex, 2026-11-18)
        p.Format.SpaceAfter = Unit.FromPoint(8);
        return p;
    }
}
