using System.IO;
using Microsoft.Win32;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Pharma.Desktop.Services;

/// <summary>Document choisi par le titulaire : nom, contenu, et genre (tableur : Excel ou CSV lu par le serveur ; sinon PDF ou image lus ici).</summary>
public sealed record DocumentBanque(string Nom, byte[] Contenu, bool EstTableur);

/// <summary>
/// Tableau d'échéances fourni par une banque : choix du fichier (Excel, CSV, PDF, image scannée) et, pour un PDF ou une image,
/// lecture du texte par l'OCR de Windows (moteur de la lecture des ordonnances), page par page, sur le poste et sans internet.
/// Le texte lu n'est qu'une proposition : il est toujours vérifié et corrigé avant l'enregistrement.
/// </summary>
public static class LectureTableauBanque
{
    private const int PagesMax = 30;

    /// <summary>Null si le titulaire annule.</summary>
    public static async Task<DocumentBanque?> ChoisirAsync()
    {
        var dialogue = new OpenFileDialog
        {
            Title = "Tableau d’amortissement fourni par la banque",
            Filter = "Tableaux d’échéances (Excel, CSV, PDF, images)|*.xlsx;*.csv;*.txt;*.pdf;*.jpg;*.jpeg;*.png;*.bmp;*.tif;*.tiff|Tous les fichiers|*.*"
        };
        if (dialogue.ShowDialog() != true) return null;
        var extension = Path.GetExtension(dialogue.FileName).ToLowerInvariant();
        return new DocumentBanque(Path.GetFileName(dialogue.FileName), await File.ReadAllBytesAsync(dialogue.FileName), extension is ".xlsx" or ".csv" or ".txt");
    }

    /// <summary>Document à joindre au prêt (contrat, tableau) : tout fichier. Null si le titulaire annule.</summary>
    public static async Task<DocumentBanque?> ChoisirDocumentAsync()
    {
        var dialogue = new OpenFileDialog { Title = "Document à joindre au prêt", Filter = "Documents|*.pdf;*.jpg;*.jpeg;*.png;*.xlsx;*.csv;*.docx|Tous les fichiers|*.*" };
        if (dialogue.ShowDialog() != true) return null;
        return new DocumentBanque(Path.GetFileName(dialogue.FileName), await File.ReadAllBytesAsync(dialogue.FileName), false);
    }

    /// <summary>Lignes de texte lues dans un PDF (toutes les pages, 30 au plus) ou une image.</summary>
    public static async Task<IReadOnlyList<string>> LireTexteAsync(DocumentBanque d)
    {
        if (!Path.GetExtension(d.Nom).Equals(".pdf", StringComparison.OrdinalIgnoreCase)) return await LectureOrdonnance.LireTexteAsync(d.Contenu);
        // Le lecteur PDF de Windows lit un fichier : copie temporaire, effacée ensuite
        var temporaire = Path.Combine(Path.GetTempPath(), $"echeancier-{Guid.NewGuid():N}.pdf");
        await File.WriteAllBytesAsync(temporaire, d.Contenu);
        try
        {
            var pdf = await PdfDocument.LoadFromFileAsync(await StorageFile.GetFileFromPathAsync(temporaire));
            var lignes = new List<string>();
            for (uint i = 0; i < Math.Min(pdf.PageCount, PagesMax); i++)
            {
                using var page = pdf.GetPage(i);
                using var flux = new InMemoryRandomAccessStream();
                // Environ 200 dpi pour une page A4, comme les ordonnances
                await page.RenderToStreamAsync(flux, new PdfPageRenderOptions { DestinationWidth = 1700 });
                var octets = new byte[flux.Size];
                using (var lecteur = new DataReader(flux.GetInputStreamAt(0)))
                {
                    await lecteur.LoadAsync((uint)flux.Size);
                    lecteur.ReadBytes(octets);
                }
                lignes.AddRange(await LectureOrdonnance.LireTexteAsync(octets));
            }
            return lignes;
        }
        finally
        {
            try { File.Delete(temporaire); } catch (IOException) { }
        }
    }
}
