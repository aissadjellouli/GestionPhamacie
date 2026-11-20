using System.Collections.Concurrent;
using PdfSharp.Fonts;

namespace Pharma.Infrastructure.Impression;

/// <summary>
/// Polices des PDF. Sous Windows, inchangé : polices installées sur le serveur (Arial). Ailleurs : Liberation Sans 2.1.5, embarquée
/// dans l'assembly (licence SIL Open Font License 1.1, Ressources/Polices/OFL-LiberationSans.txt), de mêmes métriques qu'Arial :
/// les mises en page gardent leurs dimensions. Toute famille demandée (Arial, Courier New…) est rendue en Liberation Sans.
/// </summary>
public static class PolicesPdf
{
    private static readonly Lock Verrou = new();
    private static bool configure;

    /// <summary>Réglage global de PDFsharp, une seule fois par processus (appelé par les générateurs de documents).</summary>
    public static void Configurer()
    {
        lock (Verrou)
        {
            if (configure) return;
            if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            else GlobalFontSettings.FontResolver = new PolicesEmbarquees();
            configure = true;
        }
    }
}

/// <summary>Résolveur de polices embarquées (Liberation Sans : normal, gras, italique, gras italique).</summary>
public sealed class PolicesEmbarquees : IFontResolver
{
    public const string Famille = "Liberation Sans";
    public const string Normal = "LiberationSans-Regular";
    public const string Gras = "LiberationSans-Bold";
    public const string Italique = "LiberationSans-Italic";
    public const string GrasItalique = "LiberationSans-BoldItalic";
    private static readonly ConcurrentDictionary<string, byte[]?> Cache = new();

    public FontResolverInfo ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new((isBold, isItalic) switch
        {
            (false, false) => Normal,
            (true, false) => Gras,
            (false, true) => Italique,
            _ => GrasItalique
        });

    public byte[]? GetFont(string faceName) => Cache.GetOrAdd(faceName, Lire);

    private static byte[]? Lire(string face)
    {
        using var flux = typeof(PolicesEmbarquees).Assembly.GetManifestResourceStream($"Pharma.Polices.{face}.ttf");
        if (flux is null) return null;
        using var memoire = new MemoryStream();
        flux.CopyTo(memoire);
        return memoire.ToArray();
    }
}
