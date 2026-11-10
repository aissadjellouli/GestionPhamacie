using System.IO;
using Microsoft.Win32;

namespace Pharma.Desktop.Services;

/// <summary>Fenêtres Windows de choix de dossier ou de fichier (ouvertes sur ce poste).</summary>
public static class Dialogues
{
    /// <summary>Null si l'utilisateur annule.</summary>
    public static string? ChoisirDossier(string titre, string? depart = null)
    {
        var d = new OpenFolderDialog { Title = titre, InitialDirectory = Directory.Exists(depart) ? depart : null };
        return d.ShowDialog() == true ? d.FolderName : null;
    }

    public static string? ChoisirFichier(string titre, string filtre, string? depart = null)
    {
        var d = new OpenFileDialog { Title = titre, Filter = filtre, InitialDirectory = Directory.Exists(depart) ? depart : null, CheckFileExists = true };
        return d.ShowDialog() == true ? d.FileName : null;
    }
}
