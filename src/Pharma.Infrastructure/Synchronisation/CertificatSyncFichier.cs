using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Synchronisation;
using Pharma.Infrastructure.Securite;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>
/// Clé privée et certificat client de synchronisation de CE serveur (lot S2), rangés comme la clé de scellement : dossier du PC
/// serveur (par défaut C:\ProgramData\PharmaOfficine\Synchronisation, réglage Synchronisation:Dossier), clé ECDSA P-256 générée
/// ici, chiffrée par DPAPI en portée machine (illisible sur un autre PC), fichier accessible au système, aux administrateurs et au
/// compte de l'API seulement. Jamais en base (le 2e serveur, qui reçoit la base par réplication, a son propre certificat),
/// jamais envoyée : le serveur central ne reçoit que la demande (CSR).
/// </summary>
public sealed class CertificatSyncFichier(string dossier) : ICertificatSync
{
    private static readonly byte[] Entropie = "PharmaOfficine.Synchronisation"u8.ToArray();
    public const string FichierCle = "sync-cle.dat";
    public const string FichierCleAttente = "sync-cle-attente.dat";
    public const string FichierCertificat = "sync-certificat.pem";
    public const string FichierAutorite = "sync-autorite.pem";
    private readonly Lock verrou = new();

    public string Dossier => dossier;

    public CertificatSyncInfo? Lire()
    {
        using var c = Public();
        using var a = Autorite();
        if (c is null || a is null) return null;
        return new CertificatSyncInfo(Convert.ToHexStringLower(SHA256.HashData(c.RawData)), c.NotAfter.ToUniversalTime(),
            Convert.ToHexStringLower(SHA256.HashData(a.RawData)));
    }

    public string NouvelleDemande()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=demande-synchronisation, O=PharmaOfficine", cle, HashAlgorithmName.SHA256);
        var pem = req.CreateSigningRequestPem();
        lock (verrou) Ecrire(FichierCleAttente, cle.ExportPkcs8PrivateKey());
        return pem;
    }

    public void Installer(string certificatPem, string autoritePem, string? epinglage)
    {
        lock (verrou)
        {
            using var cert = X509Certificate2.CreateFromPem(certificatPem);
            using var autorite = X509Certificate2.CreateFromPem(autoritePem);
            var empreinteAutorite = Convert.ToHexStringLower(SHA256.HashData(autorite.RawData));
            if (epinglage is not null && !empreinteAutorite.StartsWith(epinglage, StringComparison.Ordinal))
                throw new InvalidOperationException("Autorité reçue différente de celle du code d'activation.");
            if (epinglage is null && Autorite() is { } connue && !connue.RawData.AsSpan().SequenceEqual(autorite.RawData))
                throw new InvalidOperationException("Autorité reçue différente de l'autorité épinglée.");
            using var cle = ECDsa.Create();
            cle.ImportPkcs8PrivateKey(Lire(FichierCleAttente) ?? throw new InvalidOperationException("Aucune demande de certificat en attente."), out _);
            using var complet = cert.CopyWithPrivateKey(cle); // refuse si la clé ne correspond pas au certificat
            if (!Chaine(autorite, cert)) throw new InvalidOperationException("Certificat reçu non signé par l'autorité.");
            Ecrire(FichierCle, cle.ExportPkcs8PrivateKey());
            File.WriteAllText(Path.Combine(dossier, FichierCertificat), certificatPem);
            File.WriteAllText(Path.Combine(dossier, FichierAutorite), autoritePem);
            File.Delete(Path.Combine(dossier, FichierCleAttente));
        }
    }

    public X509Certificate2? CertificatClient()
    {
        lock (verrou)
        {
            using var c = Public();
            var octets = Lire(FichierCle);
            if (c is null || octets is null) return null;
            using var cle = ECDsa.Create();
            cle.ImportPkcs8PrivateKey(octets, out _);
            CryptographicOperations.ZeroMemory(octets);
            using var complet = c.CopyWithPrivateKey(cle);
            // Ré-import PKCS#12 : la pile TLS de Windows n'utilise pas une clé seulement en mémoire
            return X509CertificateLoader.LoadPkcs12(complet.Export(X509ContentType.Pkcs12), null);
        }
    }

    public X509Certificate2? Autorite()
    {
        var f = Path.Combine(dossier, FichierAutorite);
        return File.Exists(f) ? X509Certificate2.CreateFromPem(File.ReadAllText(f)) : null;
    }

    private X509Certificate2? Public()
    {
        var f = Path.Combine(dossier, FichierCertificat);
        return File.Exists(f) && File.Exists(Path.Combine(dossier, FichierCle)) ? X509Certificate2.CreateFromPem(File.ReadAllText(f)) : null;
    }

    private static bool Chaine(X509Certificate2 autorite, X509Certificate2 cert)
    {
        using var ch = new X509Chain();
        ch.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        ch.ChainPolicy.CustomTrustStore.Add(autorite);
        ch.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return ch.Build(cert);
    }

    private void Ecrire(string nom, byte[] clair)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Clé de synchronisation : Windows seulement (DPAPI).");
        Directory.CreateDirectory(dossier);
        RestreindreDossier(dossier);
        var chemin = Path.Combine(dossier, nom);
        var temporaire = chemin + ".partiel";
        File.WriteAllBytes(temporaire, ProtectedData.Protect(clair, Entropie, DataProtectionScope.LocalMachine));
        CryptographicOperations.ZeroMemory(clair);
        CompteSuperAdminFichier.Restreindre(temporaire);
        File.Move(temporaire, chemin, overwrite: true);
    }

    /// <summary>Dossier réservé au système, aux administrateurs et au compte qui exécute l'API (héritage coupé, fichiers compris).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static void RestreindreDossier(string chemin)
    {
        var droits = new System.Security.AccessControl.DirectorySecurity();
        droits.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var compte in new System.Security.Principal.IdentityReference[]
                 {
                     new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                     new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
                     System.Security.Principal.WindowsIdentity.GetCurrent().User!
                 }.Distinct())
            droits.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(compte, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
        new DirectoryInfo(chemin).SetAccessControl(droits);
    }

    private byte[]? Lire(string nom)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Clé de synchronisation : Windows seulement (DPAPI).");
        var chemin = Path.Combine(dossier, nom);
        if (!File.Exists(chemin)) return null;
        try { return ProtectedData.Unprotect(File.ReadAllBytes(chemin), Entropie, DataProtectionScope.LocalMachine); }
        catch (CryptographicException) { throw new InvalidOperationException($"Clé de synchronisation illisible sur ce PC ({chemin}) : nouvelle activation nécessaire."); }
    }
}
