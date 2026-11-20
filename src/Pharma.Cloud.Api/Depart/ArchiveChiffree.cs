using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Zip;

namespace Pharma.Cloud.Api.Depart;

/// <summary>
/// Mot de passe de l'archive de départ : tiré au sort (générateur cryptographique) à chaque export, affiché une seule fois dans la
/// console à la fin de la commande, jamais écrit dans un journal, la base, un fichier ni une exception. ToString() ne le montre pas
/// (un ResultatExport affiché ou journalisé par erreur ne le révèle pas).
/// </summary>
public sealed class MotDePasseArchive
{
    /// <summary>Alphabet sans caractères ambigus à la lecture ou à la saisie (pas de 0/O/o, 1/I/l/i).</summary>
    public const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghjkmnpqrstuvwxyz23456789";
    public const int Longueur = 24;

    private MotDePasseArchive(string valeur) => Valeur = valeur;

    /// <summary>Valeur en clair : à n'utiliser que pour chiffrer l'archive et pour l'affichage unique en fin de commande.</summary>
    public string Valeur { get; }

    public static MotDePasseArchive Tirer() => new(RandomNumberGenerator.GetString(Alphabet, Longueur));

    public override string ToString() => "(mot de passe masqué)";
}

/// <summary>
/// Zip chiffré AES-256 au format WinZip AE-2 (lisible par 7-Zip), écrit directement dans le flux de sortie : aucune copie non
/// chiffrée sur le disque. Chaque entrée est compressée (Deflate) puis chiffrée ; le nom des entrées n'est pas chiffré (format zip).
/// </summary>
public sealed class ArchiveChiffree
{
    public const int TailleCleAes = 256;
    public const string Description = "AES-256 (WinZip AE-2)";

    private readonly ZipOutputStream zip;

    public ArchiveChiffree(Stream flux, MotDePasseArchive motDePasse)
    {
        zip = new ZipOutputStream(flux) { IsStreamOwner = false, Password = motDePasse.Valeur };
        zip.SetLevel(9);
    }

    /// <summary>Ajoute une entrée chiffrée ; <paramref name="ecrire"/> écrit son contenu en clair dans le flux reçu (sans le fermer).</summary>
    public async Task<T> AjouterAsync<T>(string nom, Func<Stream, Task<T>> ecrire, CancellationToken ct)
    {
        var entree = new ZipEntry(nom) { AESKeySize = TailleCleAes, CompressionMethod = CompressionMethod.Deflated, DateTime = DateTime.Now };
        await zip.PutNextEntryAsync(entree, ct);
        var resultat = await ecrire(zip);
        await zip.CloseEntryAsync(ct);
        return resultat;
    }

    /// <summary>Écrit le répertoire central de l'archive (le flux de sortie reste ouvert).</summary>
    public async Task TerminerAsync(CancellationToken ct)
    {
        await zip.FinishAsync(ct);
        zip.Dispose();
    }
}
