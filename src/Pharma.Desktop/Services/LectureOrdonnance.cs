using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.Win32;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pharma.Desktop.Services;

/// <summary>Image d'ordonnance prête à afficher, lire et envoyer au serveur (JPEG ou PNG).</summary>
public sealed record ImageOrdonnance(byte[] Contenu, string TypeMime)
{
    public string UrlDonnees => $"data:{TypeMime};base64,{Convert.ToBase64String(Contenu)}";
}

/// <summary>
/// Capture de l'ordonnance (scanner WIA, fichier image ou PDF, caméra) et lecture du texte par l'OCR de Windows,
/// sur le poste et sans internet. L'OCR lit l'imprimé ; le manuscrit reste à saisir en regardant l'image.
/// </summary>
public static class LectureOrdonnance
{
    private const string FormatJpegWia = "{B96B3CAE-0728-11D3-9D7B-0000F81EF32E}";

    /// <summary>Numérise avec le scanner de documents (fenêtre WIA de Windows). Null si l'employé annule.</summary>
    public static async Task<ImageOrdonnance?> ScannerAsync()
    {
        var type = Type.GetTypeFromProgID("WIA.CommonDialog")
                   ?? throw new InvalidOperationException("Composant de numérisation Windows (WIA) absent de ce poste.");
        dynamic dialogue = Activator.CreateInstance(type)!;
        dynamic? image;
        try
        {
            // Scanner, couleur, qualité maximale, JPEG, sans choisir l'appareil s'il n'y en a qu'un, réglages WIA affichés
            image = dialogue.ShowAcquireImage(1, 1, 131072, FormatJpegWia, false, true, false);
        }
        catch (COMException e) when ((uint)e.HResult == 0x80210015)
        {
            throw new InvalidOperationException("Aucun scanner détecté : vérifiez qu’il est branché et allumé.");
        }
        if (image is null) return null;
        byte[] octets = (byte[])image.FileData.BinaryData;
        return await NormaliserAsync(octets);
    }

    /// <summary>Choix d'un fichier image ou PDF (première page). Null si l'employé annule.</summary>
    public static async Task<ImageOrdonnance?> ChoisirFichierAsync()
    {
        var dialogue = new OpenFileDialog
        {
            Title = "Ordonnance à importer",
            Filter = "Ordonnances (images, PDF)|*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff;*.pdf|Tous les fichiers|*.*"
        };
        if (dialogue.ShowDialog() != true) return null;
        return Path.GetExtension(dialogue.FileName).Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            ? await PremierePagePdfAsync(dialogue.FileName)
            : await NormaliserAsync(await File.ReadAllBytesAsync(dialogue.FileName));
    }

    /// <summary>Photo prise par la caméra (JPEG encodé en base64 par la page).</summary>
    public static ImageOrdonnance DepuisCamera(string base64Jpeg) => new(Convert.FromBase64String(base64Jpeg), "image/jpeg");

    /// <summary>Lignes de texte reconnues, de haut en bas.</summary>
    public static async Task<IReadOnlyList<string>> LireTexteAsync(byte[] image)
    {
        var moteur = OcrEngine.TryCreateFromLanguage(new Language("fr-FR"))
                     ?? OcrEngine.TryCreateFromUserProfileLanguages()
                     ?? throw new InvalidOperationException("Aucune langue de reconnaissance de texte installée dans Windows.");

        using var flux = new InMemoryRandomAccessStream();
        await flux.WriteAsync(image.AsBuffer());
        flux.Seek(0);
        var decodeur = await BitmapDecoder.CreateAsync(flux);

        // L'OCR refuse les images trop grandes : réduction proportionnelle si besoin
        var max = OcrEngine.MaxImageDimension;
        var echelle = Math.Min(1.0, (double)max / Math.Max(decodeur.OrientedPixelWidth, decodeur.OrientedPixelHeight));
        var transformation = new BitmapTransform
        {
            ScaledWidth = (uint)(decodeur.PixelWidth * echelle),
            ScaledHeight = (uint)(decodeur.PixelHeight * echelle),
            InterpolationMode = BitmapInterpolationMode.Fant
        };
        using var bitmap = await decodeur.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            transformation, ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage);
        var resultat = await moteur.RecognizeAsync(bitmap);
        return resultat.Lines.Select(l => l.Text).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
    }

    /// <summary>JPEG et PNG sont gardés tels quels ; les autres formats (BMP, TIFF) sont convertis en PNG.</summary>
    private static async Task<ImageOrdonnance> NormaliserAsync(byte[] octets)
    {
        if (octets is [0xFF, 0xD8, 0xFF, ..]) return new ImageOrdonnance(octets, "image/jpeg");
        if (octets is [0x89, 0x50, 0x4E, 0x47, ..]) return new ImageOrdonnance(octets, "image/png");

        using var entree = new InMemoryRandomAccessStream();
        await entree.WriteAsync(octets.AsBuffer());
        entree.Seek(0);
        var decodeur = await BitmapDecoder.CreateAsync(entree);
        using var bitmap = await decodeur.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        using var sortie = new InMemoryRandomAccessStream();
        var encodeur = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, sortie);
        encodeur.SetSoftwareBitmap(bitmap);
        await encodeur.FlushAsync();
        return new ImageOrdonnance(await LireAsync(sortie), "image/png");
    }

    private static async Task<ImageOrdonnance> PremierePagePdfAsync(string chemin)
    {
        var fichier = await StorageFile.GetFileFromPathAsync(chemin);
        var pdf = await PdfDocument.LoadFromFileAsync(fichier);
        if (pdf.PageCount == 0) throw new InvalidOperationException("Le PDF ne contient aucune page.");
        using var page = pdf.GetPage(0);
        using var sortie = new InMemoryRandomAccessStream();
        // Environ 200 dpi pour une page A4 : bon compromis lisibilité et taille
        await page.RenderToStreamAsync(sortie, new PdfPageRenderOptions { DestinationWidth = 1700 });
        return new ImageOrdonnance(await LireAsync(sortie), "image/png");
    }

    private static async Task<byte[]> LireAsync(IRandomAccessStream flux)
    {
        var octets = new byte[flux.Size];
        using var lecteur = new DataReader(flux.GetInputStreamAt(0));
        await lecteur.LoadAsync((uint)flux.Size);
        lecteur.ReadBytes(octets);
        return octets;
    }
}
