using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Ged;

/// <summary>
/// Gestion électronique des documents : un dossier du serveur (par défaut C:\ProgramData\PharmaOfficine\GED) qui regroupe
/// les ordonnances scannées, les fichiers Chifa, une copie des sauvegardes de la base, et les documents produits par les postes
/// (factures, rapports, clôtures…), rangés par type.
/// RetentionSauvegardesJours : durée de conservation par défaut de la copie des sauvegardes (réglage Ged:RetentionSauvegardesJours),
/// quand le titulaire n'en a pas choisi une dans Administration › Sauvegardes.
/// </summary>
public sealed record ReglagesGed(string Racine, int RetentionSauvegardesJours = 7)
{
    public string Dossier(string type) => Path.Combine(Racine, type);
}

public static class TypesGed
{
    public const string Ordonnances = "Ordonnances";
    public const string Chifa = "Chifa";
    public const string Sauvegardes = "Sauvegardes";

    /// <summary>Documents envoyés par les postes : un sous-dossier par type.</summary>
    public static readonly IReadOnlyList<string> DocumentsPostes =
        ["Factures", "Rapports", "Clotures", "Registre", "Commandes", "Retours", "Etiquettes", "Inventaires", "Credits", "Financements"];

    /// <summary>Nom du sous-dossier tel qu'il est écrit dans la liste (null : type inconnu).</summary>
    public static string? Normaliser(string? type) =>
        DocumentsPostes.FirstOrDefault(t => string.Equals(t, type?.Trim(), StringComparison.OrdinalIgnoreCase));
}

public interface IGed
{
    /// <summary>
    /// Range le document dans le dossier de son type, sous son nom ; renvoie « Type\nom ». Même nom et même contenu : rien n'est ajouté.
    /// Même nom, contenu différent : la version en place passe, datée, dans le sous-dossier « Versions ». Rien n'est jamais écrasé.
    /// </summary>
    Task<string> DeposerAsync(string type, string nomFichier, byte[] contenu, CancellationToken ct = default);
}

/// <summary>
/// Document produit par un poste (PDF, export Excel…), rangé dans la GED du serveur. Le type réel du fichier est contrôlé
/// (extension dans la liste des types acceptés ET contenu conforme à cette extension : signature) ; chaque dépôt est inscrit
/// au journal (employé, type, nom, taille, empreinte SHA-256 ; jamais le contenu).
/// </summary>
public sealed class DeposerDocumentGed(IGed ged, IJournal journal)
{
    public const int TailleMax = 30 * 1024 * 1024;

    /// <summary>Extensions acceptées : documents, tableurs, exports texte et images produits ou joints depuis les postes.</summary>
    public static readonly IReadOnlyList<string> ExtensionsAcceptees =
        [".pdf", ".xlsx", ".docx", ".csv", ".txt", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    public async Task<string> ExecuterAsync(long utilisateurId, string? type, string? nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        var dossier = TypesGed.Normaliser(type) ?? throw new RegleMetierException($"Type de document inconnu : {type}.");
        if (contenu.Length == 0) throw new RegleMetierException("Document vide.");
        if (contenu.Length > TailleMax) throw new RegleMetierException($"Document trop volumineux (au plus {TailleMax / 1024 / 1024} Mo).");
        var nom = NomSur(nomFichier);
        ControlerTypeReel(nom, contenu);
        var chemin = await ged.DeposerAsync(dossier, nom, contenu, ct);
        await journal.EcrireAsync(utilisateurId, "document_depose_ged", null, null, new Dictionary<string, object?>
        {
            ["type"] = dossier, ["fichier"] = nom, ["taille"] = contenu.Length,
            ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(contenu))
        }, null, ct);
        return chemin;
    }

    /// <summary>L'extension doit être acceptée et le contenu doit en porter la signature (un exécutable renommé en .pdf est refusé).</summary>
    public static void ControlerTypeReel(string nom, ReadOnlySpan<byte> c)
    {
        var extension = Path.GetExtension(nom).ToLowerInvariant();
        if (!ExtensionsAcceptees.Contains(extension))
            throw new RegleMetierException($"Type de fichier non accepté ({(extension.Length == 0 ? "sans extension" : extension)}) : PDF, Excel, Word, CSV, texte ou image.");
        var conforme = extension switch
        {
            ".pdf" => c[..Math.Min(c.Length, 1024)].IndexOf("%PDF-"u8) >= 0,
            ".xlsx" or ".docx" => Commence(c, 0x50, 0x4B, 0x03, 0x04),
            ".png" => Commence(c, 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A),
            ".jpg" or ".jpeg" => Commence(c, 0xFF, 0xD8, 0xFF),
            ".bmp" => Commence(c, 0x42, 0x4D),
            ".tif" or ".tiff" => Commence(c, 0x49, 0x49, 0x2A, 0x00) || Commence(c, 0x4D, 0x4D, 0x00, 0x2A),
            _ => EstTexte(c)
        };
        if (!conforme) throw new RegleMetierException($"Le contenu du fichier ne correspond pas à son type ({extension}) : document refusé.");
    }

    private static bool Commence(ReadOnlySpan<byte> c, params ReadOnlySpan<byte> signature) => c.StartsWith(signature);

    // Texte (CSV, TXT) : jamais d'octet nul (sauf UTF-16 avec marque d'ordre) ni d'en-tête d'exécutable (Windows MZ, Linux ELF)
    private static bool EstTexte(ReadOnlySpan<byte> c)
    {
        if (Commence(c, 0x4D, 0x5A) || Commence(c, 0x7F, 0x45, 0x4C, 0x46)) return false;
        if (Commence(c, 0xFF, 0xFE) || Commence(c, 0xFE, 0xFF)) return true;
        return c[..Math.Min(c.Length, 8192)].IndexOf((byte)0) < 0;
    }

    /// <summary>Nom de fichier sans chemin ni caractère interdit, 150 caractères au plus (extension gardée).</summary>
    public static string NomSur(string? nomFichier)
    {
        var nom = string.Concat(Path.GetFileName(nomFichier ?? "").Split(Path.GetInvalidFileNameChars())).Trim().TrimStart('.');
        if (nom.Length == 0) throw new RegleMetierException("Nom du document manquant.");
        if (nom.Length <= 150) return nom;
        var extension = Path.GetExtension(nom);
        return nom[..(150 - extension.Length)] + extension;
    }
}
