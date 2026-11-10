using System.ComponentModel;
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
        try
        {
            File.WriteAllBytes(chemin, contenu);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fichier du même nom resté ouvert (Excel, visionneuse PDF) : le document est enregistré sous un autre nom
            chemin = Path.Combine(dossier,
                $"{Path.GetFileNameWithoutExtension(chemin)} ({DateTime.Now:HHmmss}){Path.GetExtension(chemin)}");
            File.WriteAllBytes(chemin, contenu);
        }
        return chemin;
    }

    /// <summary>
    /// Ouvre le fichier avec l'application par défaut (visionneuse PDF), d'où l'on peut l'imprimer. Sans application
    /// associée, c'est son dossier qui s'ouvre : le fichier est déjà enregistré, le poste ne doit pas s'arrêter pour cela.
    /// </summary>
    public static void Ouvrir(string chemin)
    {
        try
        {
            Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{chemin}\"") { UseShellExecute = true }); }
            catch (Exception ex2) when (ex2 is Win32Exception or InvalidOperationException or IOException) { }
        }
    }
}
