using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Pharma.Cloud.Api.Securite;

/// <summary>
/// Autorité de certification propre au serveur central (lot S2) : clé ECDSA P-256 et certificat racine (10 ans) dans un dossier
/// HORS dépôt fourni en paramètre ; la clé privée y est chiffrée (PKCS#8, AES-256) par un mot de passe lu dans une variable
/// d'environnement, jamais dans appsettings ni dans git. Signe : le certificat HTTPS du serveur central, les certificats clients
/// des serveurs d'officine (3 ans, à partir de leur demande CSR : la clé privée de l'officine ne quitte jamais l'officine).
/// </summary>
public sealed class AutoriteCertification
{
    public const string FichierCertificat = "autorite.crt";
    public const string FichierCle = "autorite.cle";
    public const string FichierServeur = "serveur.pfx";
    public const string FichierCleJetons = "jetons.cle";
    public static readonly TimeSpan DureeClient = TimeSpan.FromDays(3 * 365 + 1);
    private const string OidAuthClient = "1.3.6.1.5.5.7.3.2";
    private const string OidAuthServeur = "1.3.6.1.5.5.7.3.1";
    private const string OidCleEc = "1.2.840.10045.2.1";

    private readonly X509Certificate2 racine;

    private AutoriteCertification(X509Certificate2 racine) => this.racine = racine;

    /// <summary>Certificat public de l'autorité (à épingler par les officines).</summary>
    public X509Certificate2 Racine => racine;
    public string EmpreinteRacine => Empreinte(racine);
    public string RacinePem => racine.ExportCertificatePem();

    /// <summary>Empreinte SHA-256 (hexadécimal minuscule) d'un certificat.</summary>
    public static string Empreinte(X509Certificate2 c) => Convert.ToHexStringLower(SHA256.HashData(c.RawData));

    /// <summary>Refuse un dossier situé dans un dépôt de sources (clé de l'autorité jamais dans git).</summary>
    public static void VerifierHorsDepot(string dossier)
    {
        for (var d = new DirectoryInfo(Path.GetFullPath(dossier)); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, ".git")) || File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx")))
                throw new InvalidOperationException($"Dossier {dossier} situé dans un dépôt de sources : choisissez un dossier hors dépôt.");
    }

    public static AutoriteCertification Creer(string dossier, string motDePasse, DateTimeOffset maintenant, string nom = "Autorite PharmaOfficine")
    {
        VerifierHorsDepot(dossier);
        VerifierMotDePasse(motDePasse);
        Directory.CreateDirectory(dossier);
        var crt = Path.Combine(dossier, FichierCertificat);
        if (File.Exists(crt) || File.Exists(Path.Combine(dossier, FichierCle)))
            throw new InvalidOperationException($"Une autorité existe déjà dans {dossier} : elle n'est jamais remplacée.");
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={nom}, O=PharmaOfficine", cle, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var cert = req.CreateSelfSigned(maintenant.AddMinutes(-5), maintenant.AddYears(10));
        File.WriteAllText(Path.Combine(dossier, FichierCle), cle.ExportEncryptedPkcs8PrivateKeyPem(motDePasse, Pbe()));
        File.WriteAllText(crt, cert.ExportCertificatePem());
        return Charger(dossier, motDePasse);
    }

    public static AutoriteCertification Charger(string dossier, string motDePasse)
    {
        var crt = Path.Combine(dossier, FichierCertificat);
        if (!File.Exists(crt)) throw new InvalidOperationException($"Autorité absente de {dossier} : lancez --initialiser-autorite.");
        using var publique = X509Certificate2.CreateFromPem(File.ReadAllText(crt));
        var cle = ECDsa.Create();
        try { cle.ImportFromEncryptedPem(File.ReadAllText(Path.Combine(dossier, FichierCle)), motDePasse); }
        catch (CryptographicException) { throw new InvalidOperationException("Clé de l'autorité illisible : mot de passe incorrect ?"); }
        return new AutoriteCertification(publique.CopyWithPrivateKey(cle));
    }

    /// <summary>Signe la demande (CSR PEM) d'un serveur d'officine : certificat client de 3 ans, sujet imposé par le serveur central.</summary>
    public X509Certificate2 SignerDemande(string demandePem, Guid officine, string serveur, DateTimeOffset maintenant)
    {
        CertificateRequest demande;
        try { demande = CertificateRequest.LoadSigningRequestPem(demandePem, HashAlgorithmName.SHA256, CertificateRequestLoadOptions.Default); }
        catch (Exception e) when (e is CryptographicException or ArgumentException)
        { throw new DemandeInvalideException("Demande de certificat illisible ou signature invalide."); }
        if (demande.PublicKey.Oid.Value != OidCleEc) throw new DemandeInvalideException("Clé de la demande : ECDSA P-256 attendue.");
        using (var k = demande.PublicKey.GetECDsaPublicKey())
            if (k is null || k.KeySize != 256) throw new DemandeInvalideException("Clé de la demande : ECDSA P-256 attendue.");
        var sujet = new X500DistinguishedNameBuilder();
        sujet.AddOrganizationName("PharmaOfficine");
        sujet.AddOrganizationalUnitName(serveur);
        sujet.AddCommonName(officine.ToString("D"));
        var req = new CertificateRequest(sujet.Build(), demande.PublicKey, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(OidAuthClient)], true));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(racine, true, false));
        var fin = maintenant.Add(DureeClient);
        if (fin > racine.NotAfter) fin = racine.NotAfter;
        return req.Create(racine, maintenant.AddMinutes(-5), fin, NumeroSerie());
    }

    /// <summary>Certificat HTTPS du serveur central (2 ans), exporté en PFX protégé par le mot de passe.</summary>
    public void CreerCertificatServeur(string dossier, string motDePasse, IReadOnlyList<string> noms, DateTimeOffset maintenant)
    {
        VerifierHorsDepot(dossier);
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={noms[0]}, O=PharmaOfficine", cle, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in noms)
            if (System.Net.IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(n);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(OidAuthServeur)], true));
        req.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(racine, true, false));
        using var cert = req.Create(racine, maintenant.AddMinutes(-5), maintenant.AddYears(2), NumeroSerie());
        using var complet = cert.CopyWithPrivateKey(cle);
        File.WriteAllBytes(Path.Combine(dossier, FichierServeur), complet.Export(X509ContentType.Pfx, motDePasse));
    }

    /// <summary>Certificat client émis par cette autorité, valide à cette date, pour l'authentification client.</summary>
    public bool ValiderClient(X509Certificate2 c, DateTimeOffset maintenant)
    {
        using var chaine = new X509Chain();
        chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chaine.ChainPolicy.CustomTrustStore.Add(racine);
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck; // révocation : base centrale, à chaque appel
        chaine.ChainPolicy.VerificationTime = maintenant.UtcDateTime;
        chaine.ChainPolicy.ApplicationPolicy.Add(new Oid(OidAuthClient));
        return chaine.Build(c) && chaine.ChainElements[^1].Certificate.RawData.AsSpan().SequenceEqual(racine.RawData);
    }

    public static void VerifierMotDePasse(string? motDePasse)
    {
        if (string.IsNullOrEmpty(motDePasse) || motDePasse.Length < 16)
            throw new InvalidOperationException("Mot de passe de l'autorité : 16 caractères au moins (variable d'environnement PHARMA_CLOUD_MOT_DE_PASSE).");
    }

    internal static PbeParameters Pbe() => new(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000);

    private static byte[] NumeroSerie()
    {
        var s = RandomNumberGenerator.GetBytes(16);
        s[0] &= 0x7F; // positif
        return s;
    }
}

public sealed class DemandeInvalideException(string message) : Exception(message);
