using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.SecondServeur;

// ---- Clé de scellement : export protégé par mot de passe et import sur le 2e serveur (2026-11-16) -----------------------
// Le chiffrement porte sur la CLÉ seulement (fichier d'export), jamais sur les données de la base.

/// <summary>Clé de scellement brute gardée sur ce PC (fichier Integrite:FichierCle, chiffré DPAPI machine).</summary>
public interface ICleIntegriteStockee
{
    string Emplacement { get; }
    bool Presente { get; }
    /// <summary>Copie de la clé (32 octets) ; à effacer après usage. Créée si elle n'existe pas encore.</summary>
    byte[] Lire();
    /// <summary>Écrit la clé (rechiffrée DPAPI machine) ; une clé existante différente n'est remplacée que si remplacer (ancienne gardée à côté).</summary>
    void Ecrire(byte[] cle, bool remplacer);
}

/// <summary>
/// Fichier d'export de la clé : « PHOCLE01 » | itérations (4 octets) | sel (16) | nonce (12) | étiquette (16) | clé chiffrée.
/// Clé de chiffrement tirée du mot de passe par PBKDF2-SHA256 ; chiffrement AES-256-GCM (en-tête authentifié).
/// </summary>
public static class CoffreCle
{
    public const int LongueurMinimale = 12;
    public const int IterationsParDefaut = 600_000;
    public const int TailleCle = 32;
    private const int IterationsMin = 100_000, IterationsMax = 10_000_000;
    private static readonly byte[] Entete = "PHOCLE01"u8.ToArray();
    private const int TailleSel = 16, TailleNonce = 12, TailleEtiquette = 16;
    private static readonly int TailleEnTete = Entete.Length + 4 + TailleSel;

    /// <summary>Mot de passe choisi par le titulaire : 12 caractères au moins, saisi deux fois.</summary>
    public static void ControlerMotDePasse(string? motDePasse, string? confirmation)
    {
        if (string.IsNullOrEmpty(motDePasse) || motDePasse.Length < LongueurMinimale)
            throw new RegleMetierException($"Mot de passe du fichier de clé : {LongueurMinimale} caractères au moins.", "mot_de_passe");
        if (motDePasse != confirmation)
            throw new RegleMetierException("La confirmation ne correspond pas au mot de passe.", "confirmation");
    }

    public static byte[] Chiffrer(ReadOnlySpan<byte> cle, string motDePasse, int iterations = IterationsParDefaut)
    {
        if (cle.Length != TailleCle) throw new ArgumentException("Clé de scellement invalide.", nameof(cle));
        if (iterations is < IterationsMin or > IterationsMax) throw new ArgumentOutOfRangeException(nameof(iterations));
        var fichier = new byte[TailleEnTete + TailleNonce + TailleEtiquette + TailleCle];
        Entete.CopyTo(fichier, 0);
        BinaryPrimitives.WriteInt32BigEndian(fichier.AsSpan(Entete.Length, 4), iterations);
        RandomNumberGenerator.Fill(fichier.AsSpan(Entete.Length + 4, TailleSel));
        RandomNumberGenerator.Fill(fichier.AsSpan(TailleEnTete, TailleNonce));
        var cleAes = Deriver(motDePasse, fichier.AsSpan(Entete.Length + 4, TailleSel), iterations);
        try
        {
            using var aes = new AesGcm(cleAes, TailleEtiquette);
            aes.Encrypt(fichier.AsSpan(TailleEnTete, TailleNonce), cle, fichier.AsSpan(TailleEnTete + TailleNonce + TailleEtiquette),
                fichier.AsSpan(TailleEnTete + TailleNonce, TailleEtiquette), fichier.AsSpan(0, TailleEnTete));
        }
        finally { CryptographicOperations.ZeroMemory(cleAes); }
        return fichier;
    }

    /// <summary>Clé déchiffrée ; RegleMetierException si le mot de passe est faux ou le fichier abîmé (sans dire lequel).</summary>
    public static byte[] Dechiffrer(byte[] fichier, string motDePasse)
    {
        if (fichier is null || fichier.Length != TailleEnTete + TailleNonce + TailleEtiquette + TailleCle || !fichier.AsSpan(0, Entete.Length).SequenceEqual(Entete))
            throw new RegleMetierException("Ce fichier n'est pas un export de clé de scellement Pharma Officine.");
        var iterations = BinaryPrimitives.ReadInt32BigEndian(fichier.AsSpan(Entete.Length, 4));
        if (iterations is < IterationsMin or > IterationsMax)
            throw new RegleMetierException("Fichier de clé abîmé.");
        var cleAes = Deriver(motDePasse ?? "", fichier.AsSpan(Entete.Length + 4, TailleSel), iterations);
        var cle = new byte[TailleCle];
        try
        {
            using var aes = new AesGcm(cleAes, TailleEtiquette);
            aes.Decrypt(fichier.AsSpan(TailleEnTete, TailleNonce), fichier.AsSpan(TailleEnTete + TailleNonce + TailleEtiquette),
                fichier.AsSpan(TailleEnTete + TailleNonce, TailleEtiquette), cle, fichier.AsSpan(0, TailleEnTete));
            return cle;
        }
        catch (AuthenticationTagMismatchException)
        {
            CryptographicOperations.ZeroMemory(cle);
            throw new RegleMetierException("Mot de passe incorrect ou fichier de clé abîmé : la clé n'a pas été importée.", "mot_de_passe");
        }
        finally { CryptographicOperations.ZeroMemory(cleAes); }
    }

    /// <summary>Empreinte publique de la clé (16 premiers caractères du SHA-256), comme ScelleurIntegrite.EmpreinteCle.</summary>
    public static string Empreinte(ReadOnlySpan<byte> cle) => Convert.ToHexStringLower(SHA256.HashData(cle))[..16];

    private static byte[] Deriver(string motDePasse, ReadOnlySpan<byte> sel, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(motDePasse), sel, iterations, HashAlgorithmName.SHA256, 32);
}

public sealed record FichierCleExporte(string NomFichier, byte[] Contenu, string Empreinte);

/// <summary>
/// Export de la clé de scellement (titulaire seul) : fichier chiffré par le mot de passe choisi, à ranger hors du PC (clé USB au
/// coffre). Journal des actions : empreinte et nom du fichier, jamais la clé ni le mot de passe.
/// </summary>
public sealed class ExporterCleScellement(ICleIntegriteStockee cle, IJournal journal, IHorloge horloge)
{
    public const string Action = "export_cle_scellement";

    public async Task<FichierCleExporte> ExecuterAsync(string? motDePasse, string? confirmation, long utilisateurId, CancellationToken ct = default)
    {
        CoffreCle.ControlerMotDePasse(motDePasse, confirmation);
        var brute = cle.Lire();
        try
        {
            var empreinte = CoffreCle.Empreinte(brute);
            var contenu = CoffreCle.Chiffrer(brute, motDePasse!);
            var nom = $"cle-scellement-{horloge.Aujourdhui:yyyyMMdd}-{empreinte}.pocle";
            await journal.EcrireAsync(utilisateurId, Action, null, null,
                new Dictionary<string, object?> { ["empreinte"] = empreinte, ["fichier"] = nom }, null, ct);   // titulaire seul (groupe ExigerTitulaire) : pas de code de droit, la clé étrangère de journal_audit n'accepte que les droits du catalogue
            return new FichierCleExporte(nom, contenu, empreinte);
        }
        finally { CryptographicOperations.ZeroMemory(brute); }
    }
}

public sealed record ResultatImportCle(string Empreinte, bool DejaPresente, string Message);

/// <summary>
/// Import de la clé sur le 2e serveur (Pharma.Api.exe --importer-cle, lancé sur ce serveur ; mot de passe tapé par le titulaire) :
/// rechiffrée DPAPI machine dans Integrite:FichierCle. Une autre clé déjà présente n'est remplacée que sur demande.
/// Le message rendu (journal du serveur) ne contient que l'empreinte.
/// </summary>
public sealed class ImporterCleScellement(ICleIntegriteStockee cle)
{
    public ResultatImportCle Executer(byte[] fichier, string? motDePasse, bool remplacer)
    {
        var importee = CoffreCle.Dechiffrer(fichier, motDePasse ?? "");
        try
        {
            var empreinte = CoffreCle.Empreinte(importee);
            if (cle.Presente)
            {
                var actuelle = cle.Lire();
                try
                {
                    if (CryptographicOperations.FixedTimeEquals(actuelle, importee))
                        return new ResultatImportCle(empreinte, true, $"Clé de scellement déjà présente et identique (empreinte {empreinte}) : rien à faire.");
                    if (!remplacer)
                        throw new RegleMetierException($"Une autre clé de scellement existe sur ce PC (empreinte {CoffreCle.Empreinte(actuelle)}). "
                            + "Elle n'est remplacée qu'avec l'option --remplacer (l'ancienne est gardée à côté).");
                }
                finally { CryptographicOperations.ZeroMemory(actuelle); }
            }
            cle.Ecrire(importee, remplacer);
            return new ResultatImportCle(empreinte, false, $"Clé de scellement importée (empreinte {empreinte}) dans {cle.Emplacement}.");
        }
        finally { CryptographicOperations.ZeroMemory(importee); }
    }
}
