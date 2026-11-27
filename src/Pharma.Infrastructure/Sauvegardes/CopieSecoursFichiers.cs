using System.Security.Cryptography;
using System.Text.Json;
using Pharma.Application.Sauvegardes;

namespace Pharma.Infrastructure.Sauvegardes;

/// <summary>
/// Réglages et état de la copie de secours dans un fichier du PC serveur (par défaut C:\ProgramData\PharmaOfficine\copie-secours.json),
/// hors de la base : ils restent lisibles si la base est perdue. Aucun secret dedans (chemins, dates, empreintes).
/// </summary>
public sealed class CopieSecoursFichier(string chemin) : ICopieSecoursDepot
{
    [System.Reflection.Obfuscation(Exclude = true, ApplyToMembers = true)]
    private sealed record Contenu(ReglagesCopieSecours? Reglages, EtatCopieSecours? Etat);

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly Lock Verrou = new();

    public string Emplacement => chemin;

    public ReglagesCopieSecours Reglages() => Lire().Reglages ?? new ReglagesCopieSecours();
    public EtatCopieSecours Etat() => Lire().Etat ?? new EtatCopieSecours();

    public void EnregistrerReglages(ReglagesCopieSecours reglages) { lock (Verrou) Ecrire(Lire() with { Reglages = reglages }); }
    public void EnregistrerEtat(EtatCopieSecours etat) { lock (Verrou) Ecrire(Lire() with { Etat = etat }); }

    private Contenu Lire()
    {
        lock (Verrou)
        {
            try
            {
                return File.Exists(chemin) ? JsonSerializer.Deserialize<Contenu>(File.ReadAllText(chemin)) ?? new(null, null) : new(null, null);
            }
            catch (JsonException) { return new(null, null); }   // fichier abîmé : réglages par défaut (pas de copie), à refaire à l'écran
        }
    }

    private void Ecrire(Contenu c)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        var temporaire = chemin + ".partiel";
        File.WriteAllText(temporaire, JsonSerializer.Serialize(c, Options));
        File.Move(temporaire, chemin, overwrite: true);
    }
}

/// <summary>Fichiers du second emplacement : copie complète ou rien, empreinte SHA-256 notée dans « &lt;copie&gt;.sha256 ».</summary>
public sealed class FichiersCopieSecours : IFichiersCopieSecours
{
    public const string ExtensionEmpreinte = ".sha256";

    public bool Accessible(string dossier)
    {
        var racine = Path.GetPathRoot(dossier);
        if (string.IsNullOrEmpty(racine)) return false;
        try
        {
            if (!racine.StartsWith(@"\\", StringComparison.Ordinal) && !new DriveInfo(racine).IsReady) return false;
            Directory.CreateDirectory(dossier);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    public void Copier(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporaire = destination + ".partiel";
        File.Copy(source, temporaire, overwrite: true);
        File.Move(temporaire, destination, overwrite: true);
    }

    public long Taille(string fichier) => new FileInfo(fichier).Length;

    public string Sha256(string fichier)
    {
        using var flux = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        return Convert.ToHexStringLower(SHA256.HashData(flux));
    }

    // Format de sha256sum (« empreinte  nom ») : lisible aussi par Get-FileHash dans le script de bascule
    public string? EmpreinteNotee(string copie)
    {
        var f = copie + ExtensionEmpreinte;
        if (!File.Exists(f)) return null;
        var texte = File.ReadAllText(f).Trim();
        var espace = texte.IndexOf(' ');
        return espace > 0 ? texte[..espace] : texte;
    }

    public void NoterEmpreinte(string copie, string sha256) =>
        File.WriteAllText(copie + ExtensionEmpreinte, $"{sha256}  {Path.GetFileName(copie)}{Environment.NewLine}");

    public IReadOnlyList<string> Copies(string dossier) =>
        Directory.Exists(dossier)
            ? new DirectoryInfo(dossier).GetFiles("pharmacie_*.dump").OrderByDescending(f => f.Name, StringComparer.Ordinal).Select(f => f.FullName).ToList()
            : [];

    public bool Existe(string fichier) => File.Exists(fichier);

    public void Supprimer(string copie)
    {
        File.Delete(copie);
        File.Delete(copie + ExtensionEmpreinte);
    }
}
