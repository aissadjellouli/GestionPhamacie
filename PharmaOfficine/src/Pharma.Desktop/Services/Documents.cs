using System.Diagnostics;
using System.IO;

namespace Pharma.Desktop.Services;

/// <summary>Documents reçus du serveur (PDF), enregistrés dans Documents\Pharma Officine puis ouverts.</summary>
public static class Documents
{
    public static string Enregistrer(string sousDossier, string nomFichier, byte[] contenu)
    {
        var dossier = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pharma Officine", sousDossier);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, string.Concat(nomFichier.Split(Path.GetInvalidFileNameChars())));
        File.WriteAllBytes(chemin, contenu);
        return chemin;
    }

    /// <summary>Ouvre le fichier avec l'application par défaut (visionneuse PDF), d'où l'on peut l'imprimer.</summary>
    public static void Ouvrir(string chemin) =>
        Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });
}
