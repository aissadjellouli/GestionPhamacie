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
        ["Factures", "Rapports", "Clotures", "Registre", "Commandes", "Retours", "Etiquettes", "Inventaires", "Credits"];

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

/// <summary>Document produit par un poste (PDF, export Excel), rangé dans la GED du serveur.</summary>
public sealed class DeposerDocumentGed(IGed ged)
{
    public const int TailleMax = 30 * 1024 * 1024;

    public Task<string> ExecuterAsync(string? type, string? nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        var dossier = TypesGed.Normaliser(type) ?? throw new RegleMetierException($"Type de document inconnu : {type}.");
        if (contenu.Length == 0) throw new RegleMetierException("Document vide.");
        if (contenu.Length > TailleMax) throw new RegleMetierException($"Document trop volumineux (au plus {TailleMax / 1024 / 1024} Mo).");
        return ged.DeposerAsync(dossier, NomSur(nomFichier), contenu, ct);
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
