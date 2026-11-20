using System.Security.Cryptography;
using Pharma.Application.Integrite;

namespace Pharma.Infrastructure.Securite;

/// <summary>
/// Adaptateur local du scellement : clé secrète de 32 octets aléatoires dans un fichier du PC serveur (par défaut
/// C:\ProgramData\PharmaOfficine\cle-integrite.dat), chiffrée par DPAPI en portée machine (illisible sur un autre PC) et accessible
/// seulement au système, aux administrateurs et au compte de l'API ; hors Windows, chiffrée par DataProtection (IProtectionDonnees). Créée au premier usage si elle n'existe pas. Jamais en base,
/// jamais dans appsettings, jamais journalisée : seule son empreinte (EmpreinteCle) est montrée.
/// Si le fichier est perdu, une nouvelle clé est créée : les anciens sceaux sont alors signalés « faits avec une autre clé »
/// (la clé se sauvegarde avec le PC serveur, docs/securite-et-droits.md).
/// </summary>
public sealed class ScelleurIntegriteLocal(string chemin, IProtectionDonnees protection) : IScelleurIntegrite, Application.SecondServeur.ICleIntegriteStockee
{
    private const string Usage = "PharmaOfficine.Integrite";
    private const int TailleCle = 32;
    private readonly Lock verrou = new();
    private byte[]? cle;

    public string Emplacement => chemin;
    public string Algorithme => "HMAC-SHA256";
    public string EmpreinteCle => Convert.ToHexStringLower(SHA256.HashData(Cle()))[..16];

    public byte[] Sceller(byte[] message) => HMACSHA256.HashData(Cle(), message);

    public bool Verifier(byte[] message, byte[] sceau) => CryptographicOperations.FixedTimeEquals(Sceller(message), sceau);

    // ---- Export / import de la clé (2e serveur, 2026-11-16) : ICleIntegriteStockee ----
    public bool Presente => File.Exists(chemin);

    /// <summary>Copie de la clé, à effacer par l'appelant.</summary>
    public byte[] Lire() => (byte[])Cle().Clone();

    /// <summary>Clé importée, rechiffrée (DPAPI machine sous Windows) ; une clé existante n'est remplacée que si remplacer (ancienne gardée à côté, datée).</summary>
    public void Ecrire(byte[] nouvelle, bool remplacer)
    {
        if (nouvelle.Length != TailleCle) throw new ArgumentException("Clé de scellement invalide.", nameof(nouvelle));
        lock (verrou)
        {
            if (File.Exists(chemin) && !remplacer) throw new InvalidOperationException($"Une clé de scellement existe déjà ({chemin}).");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(chemin))!);
            var temporaire = chemin + ".partiel";
            File.WriteAllBytes(temporaire, protection.Proteger(nouvelle, Usage));
            FichiersProteges.Restreindre(temporaire);
            if (File.Exists(chemin)) File.Copy(chemin, $"{chemin}.avant-import-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: false);
            File.Move(temporaire, chemin, overwrite: true);
            if (cle is not null) CryptographicOperations.ZeroMemory(cle);
            cle = (byte[])nouvelle.Clone();
        }
    }

    private byte[] Cle()
    {
        lock (verrou)
        {
            if (cle is not null) return cle;
            if (File.Exists(chemin))
            {
                try
                {
                    cle = protection.Deproteger(File.ReadAllBytes(chemin), Usage);
                }
                catch (CryptographicException)
                {
                    // Jamais remplacée en silence : les sceaux existants deviendraient invérifiables
                    throw new InvalidOperationException($"Clé de scellement illisible sur ce PC ({chemin}) : restaurez le fichier d'origine.");
                }
                if (cle.Length != TailleCle) throw new InvalidOperationException($"Clé de scellement invalide ({chemin}).");
                return cle;
            }
            var nouvelle = RandomNumberGenerator.GetBytes(TailleCle);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(chemin))!);
            // Écrit à côté puis renomme : jamais de fichier à moitié écrit ; sans écraser une clé créée entre-temps
            var temporaire = chemin + ".partiel";
            File.WriteAllBytes(temporaire, protection.Proteger(nouvelle, Usage));
            FichiersProteges.Restreindre(temporaire);
            File.Move(temporaire, chemin, overwrite: false);
            cle = nouvelle;
            return cle;
        }
    }
}
