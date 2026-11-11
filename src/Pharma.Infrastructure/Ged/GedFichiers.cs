using System.Security.Cryptography;
using Pharma.Application.Ged;

namespace Pharma.Infrastructure.Ged;

/// <summary>
/// GED en fichiers sur le disque du serveur : un sous-dossier par type de document.
/// Un document garde son nom simple pour sa dernière version ; les versions précédentes passent dans le sous-dossier
/// « Versions » du type, datées de leur dépôt. Un dépôt identique (même nom, même contenu) n'ajoute rien. Rien n'est écrasé.
/// </summary>
public sealed class GedFichiers(ReglagesGed reglages) : IGed
{
    public const string SousDossierVersions = "Versions";

    /// <summary>Un dépôt à la fois : le passage de l'ancienne version dans « Versions » et l'arrivée de la nouvelle vont ensemble.</summary>
    private readonly SemaphoreSlim verrou = new(1, 1);

    public async Task<string> DeposerAsync(string type, string nomFichier, byte[] contenu, CancellationToken ct = default)
    {
        var dossier = reglages.Dossier(type);
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, nomFichier);
        var resultat = Path.Combine(type, nomFichier);
        // Écrit sous un nom temporaire puis renommé : un document n'apparaît qu'une fois complet
        var temporaire = Path.Combine(dossier, $".{Guid.NewGuid():N}.partiel");
        await File.WriteAllBytesAsync(temporaire, contenu, ct);
        await verrou.WaitAsync(ct);
        try
        {
            if (File.Exists(chemin))
            {
                if (await IdentiqueAsync(chemin, contenu, ct))
                {
                    File.Delete(temporaire);
                    return resultat;
                }
                RangerVersion(dossier, chemin);
            }
            File.Move(temporaire, chemin, overwrite: false);
            return resultat;
        }
        catch
        {
            try { File.Delete(temporaire); } catch (IOException) { }
            throw;
        }
        finally
        {
            verrou.Release();
        }
    }

    /// <summary>Même taille et même empreinte SHA-256.</summary>
    private static async Task<bool> IdentiqueAsync(string chemin, byte[] contenu, CancellationToken ct)
    {
        if (new FileInfo(chemin).Length != contenu.Length) return false;
        await using var flux = File.OpenRead(chemin);
        return (await SHA256.HashDataAsync(flux, ct)).AsSpan().SequenceEqual(SHA256.HashData(contenu));
    }

    /// <summary>Déplace la version en place dans « Versions », sous « nom AAAA-MM-JJ HHhmm.ext » (date de son dépôt), sans rien écraser.</summary>
    private static void RangerVersion(string dossier, string chemin)
    {
        var versions = Path.Combine(dossier, SousDossierVersions);
        Directory.CreateDirectory(versions);
        var nomDate = NomVersion(Path.GetFileName(chemin), File.GetLastWriteTime(chemin));
        for (var n = 1; ; n++)
        {
            var cible = Path.Combine(versions, NomNumero(nomDate, n));
            try
            {
                File.Move(chemin, cible, overwrite: false);
                return;
            }
            catch (IOException) when (File.Exists(cible) && n < 1000) { }
        }
    }

    /// <summary>« F-2026-0021.pdf » déposé le 11/11/2026 à 14:47 : « F-2026-0021 2026-11-11 14h47.pdf ».</summary>
    internal static string NomVersion(string nom, DateTime depot) =>
        $"{Path.GetFileNameWithoutExtension(nom)} {depot:yyyy-MM-dd HH}h{depot:mm}{Path.GetExtension(nom)}";

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
