using Pharma.Application.Ged;

namespace Pharma.Infrastructure.Ged;

/// <summary>GED en fichiers sur le disque du serveur : un sous-dossier par type de document.</summary>
public sealed class GedFichiers(ReglagesGed reglages) : IGed
{
    public async Task<string> DeposerAsync(string type, string nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        var dossier = reglages.Dossier(type);
        Directory.CreateDirectory(dossier);
        // Écrit sous un nom temporaire puis renommé : un document n'apparaît qu'une fois complet, et n'en écrase jamais un autre
        var temporaire = Path.Combine(dossier, $".{Guid.NewGuid():N}.partiel");
        await File.WriteAllBytesAsync(temporaire, contenu, ct);
        for (var n = 1; ; n++)
        {
            var chemin = Path.Combine(dossier, NomNumero(nomFichier, n));
            try
            {
                File.Move(temporaire, chemin, overwrite: false);
                return Path.Combine(type, Path.GetFileName(chemin));
            }
            catch (IOException) when (File.Exists(chemin) && n < 1000) { }
        }
    }

    /// <summary>nom.pdf, puis « nom (2).pdf », « nom (3).pdf »…</summary>
    internal static string NomNumero(string nom, int n) =>
        n == 1 ? nom : $"{Path.GetFileNameWithoutExtension(nom)} ({n}){Path.GetExtension(nom)}";

    /// <summary>
    /// Déplace le contenu d'un ancien dossier dans la GED (sous-dossiers compris), sans rien écraser : un fichier du même nom
    /// déjà présent garde le sien, le fichier déplacé prend « (2) ». L'ancien dossier est supprimé s'il est vide. Renvoie le nombre
    /// de fichiers déplacés.
    /// </summary>
    public static int Deplacer(string source, string destination)
    {
        if (!Directory.Exists(source) || Path.GetFullPath(source).TrimEnd('\\') == Path.GetFullPath(destination).TrimEnd('\\')) return 0;
        var deplaces = 0;
        foreach (var fichier in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).ToList())
        {
            var relatif = Path.GetRelativePath(source, fichier);
            var dossier = Path.Combine(destination, Path.GetDirectoryName(relatif) ?? "");
            Directory.CreateDirectory(dossier);
            for (var n = 1; n < 1000; n++)
            {
                var cible = Path.Combine(dossier, NomNumero(Path.GetFileName(relatif), n));
                if (File.Exists(cible)) continue;
                try { File.Move(fichier, cible); deplaces++; }
                catch (IOException) { /* fichier ouvert ailleurs : laissé en place, réessayé au prochain démarrage */ }
                break;
            }
        }
        foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).Append(source))
            try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch (IOException) { }
        return deplaces;
    }
}
