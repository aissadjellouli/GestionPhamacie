using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Pharma.Desktop.Services;

/// <summary>
/// Documents produits au poste (PDF, exports Excel) : rangés dans la GED du serveur (C:\ProgramData\PharmaOfficine\GED\&lt;Type&gt;),
/// et ouverts depuis une copie temporaire du poste. Serveur injoignable : le document est gardé au poste (dossier « A envoyer »)
/// et envoyé à la GED à la connexion suivante.
/// </summary>
public static class Documents
{
    /// <summary>Types de documents (sous-dossiers de la GED), les mêmes que ceux acceptés par le serveur.</summary>
    public static readonly string[] Types = ["Factures", "Rapports", "Clotures", "Registre", "Commandes", "Retours", "Etiquettes", "Inventaires", "Credits"];

    /// <summary>CheminLocal : copie à ouvrir au poste ; CheminGed : « Type\nom » dans la GED (null : pas encore envoyé, voir Erreur).</summary>
    public sealed record DocumentRange(string CheminLocal, string? CheminGed, string? Erreur)
    {
        public string Emplacement => CheminGed is { } g ? $"GED du serveur, {g}" : CheminLocal;
    }

    private static string DossierTemporaire => Path.Combine(Path.GetTempPath(), "PharmaOfficine");
    private static string DossierAEnvoyer =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmaOfficine", "A envoyer");
    // Ancien emplacement des documents (avant la GED) : vidé dans la GED à la première connexion
    private static string AncienDossier =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Pharma Officine");

    public static async Task<DocumentRange> RangerAsync(ApiClient api, string type, string nomFichier, byte[] contenu)
    {
        var nom = string.Concat(nomFichier.Split(Path.GetInvalidFileNameChars()));
        var (ged, erreur) = await api.DeposerGedAsync(type, nom, contenu);
        if (erreur is null)
            return new DocumentRange(Ecrire(Path.Combine(DossierTemporaire, type), nom, contenu), ged, null);
        // Pas rangé : gardé au poste jusqu'à la prochaine connexion (pas dans le dossier temporaire, que Windows peut vider)
        var local = Ecrire(Path.Combine(DossierAEnvoyer, type), nom, contenu);
        return new DocumentRange(local, null, $"Document non rangé dans la GED du serveur ({erreur}) : gardé sur ce poste et envoyé à la prochaine connexion.");
    }

    private static string Ecrire(string dossier, string nom, byte[] contenu)
    {
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nom);
        try
        {
            File.WriteAllBytes(chemin, contenu);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Fichier du même nom resté ouvert (Excel, visionneuse PDF) : le document est enregistré sous un autre nom
            chemin = Path.Combine(dossier, $"{Path.GetFileNameWithoutExtension(nom)} ({DateTime.Now:HHmmss}){Path.GetExtension(nom)}");
            File.WriteAllBytes(chemin, contenu);
        }
        return chemin;
    }

    private static int migration;

    /// <summary>
    /// Une fois par lancement, après la connexion : documents de l'ancien dossier Documents\Pharma Officine et documents restés
    /// au poste (serveur injoignable) envoyés à la GED du serveur, puis supprimés du poste. Le serveur n'écrase jamais un document.
    /// </summary>
    public static async Task<int> MigrerAsync(ApiClient api)
    {
        if (Interlocked.Exchange(ref migration, 1) == 1) return 0;
        var envoyes = 0;
        foreach (var racine in new[] { AncienDossier, DossierAEnvoyer })
        {
            if (!Directory.Exists(racine)) continue;
            foreach (var type in Types)
            {
                var dossier = Path.Combine(racine, type);
                if (!Directory.Exists(dossier)) continue;
                foreach (var f in Directory.EnumerateFiles(dossier).ToList())
                {
                    byte[] contenu;
                    try { contenu = await File.ReadAllBytesAsync(f); } catch (IOException) { continue; }   // ouvert ailleurs : plus tard
                    if (contenu.Length == 0) continue;
                    var (_, erreur) = await api.DeposerGedAsync(type, Path.GetFileName(f), contenu);
                    if (erreur is not null) { migration = 0; return envoyes; }   // serveur injoignable : réessayé à la prochaine connexion
                    try { File.Delete(f); envoyes++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                try { if (!Directory.EnumerateFileSystemEntries(dossier).Any()) Directory.Delete(dossier); } catch (IOException) { }
            }
            try { if (!Directory.EnumerateFileSystemEntries(racine).Any()) Directory.Delete(racine); } catch (IOException) { }
        }
        return envoyes;
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
