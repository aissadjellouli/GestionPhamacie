using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pharma.Application.Offre;
using Pharma.Infrastructure.Securite;

namespace Pharma.Infrastructure.Offre;

/// <summary>Clé de ce serveur telle que trouvée ou créée : fournisseur et empreinte.</summary>
/// <param name="Fournisseur">Fournisseur de la clé (« Microsoft Platform Crypto Provider » pour le TPM).</param>
/// <param name="Materielle">Vrai si la clé est protégée par le TPM.</param>
/// <param name="Creee">Vrai si la clé vient d'être créée (faux : clé existante, jamais remplacée).</param>
public sealed record InfoCleServeur(string Fournisseur, bool Materielle, string Empreinte, bool Creee);

/// <summary>Clé de ce serveur (point 5 du 2026-11-27) : CNG sous Windows (TPM, sinon logicielle non exportable), fichier ailleurs.</summary>
public static class CleServeurLocale
{
    public static ICleServeurLocale Creer(string racineDonnees) =>
        OperatingSystem.IsWindows() ? new CleServeurCng() : new CleServeurFichier(Path.Combine(racineDonnees, "Serveur", CleServeurFichier.FichierNom));
}

/// <summary>Clé de ce serveur, avec la création (commande d'administrateur) et la lecture de l'empreinte (commandes de l'API).</summary>
public interface ICleServeurLocale : ICleServeur
{
    /// <summary>Clé existante (fournisseur, empreinte), null si absente.</summary>
    InfoCleServeur? Lire();
    /// <summary>Crée la clé si elle n'existe pas (idempotent : une clé existante n'est jamais remplacée). Échec : InvalidOperationException.</summary>
    InfoCleServeur CreerSiAbsente();
}

/// <summary>
/// Clé CNG de MACHINE nommée « PharmaOfficine-Serveur », NON EXPORTABLE, usage signature : fournisseur TPM (« Microsoft Platform Crypto Provider »)
/// essayé d'abord, sinon fournisseur logiciel (« Microsoft Software Key Storage Provider », avec avertissement). Droits posés à la création :
/// SYSTEM (compte du service « PharmaOfficine API », LocalSystem par défaut) et Administrateurs seulement. La création demande les droits
/// d'administrateur (clé de machine) ; l'API ne fait qu'ouvrir la clé, à chaque évaluation de l'offre (au plus une fois par minute).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CleServeurCng : ICleServeurLocale
{
    public const string Nom = "PharmaOfficine-Serveur";
    public static readonly CngProvider Tpm = new("Microsoft Platform Crypto Provider");
    private static readonly CngProvider[] Fournisseurs = [Tpm, CngProvider.MicrosoftSoftwareKeyStorageProvider];

    // SYSTEM et Administrateurs : tout ; personne d'autre (un compte Windows standard du PC ne peut pas s'en servir)
    private const string Droits = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";
    private const CngPropertyOptions DaclSecurityInformation = (CngPropertyOptions)4;

    private static (ECDsaCng Cle, CngProvider Fournisseur)? Ouvrir()
    {
        foreach (var f in Fournisseurs)
        {
            try
            {
                if (CngKey.Exists(Nom, f, CngKeyOpenOptions.MachineKey)) return (new ECDsaCng(CngKey.Open(Nom, f, CngKeyOpenOptions.MachineKey)), f);
            }
            catch (CryptographicException) { /* fournisseur absent (pas de TPM), clé illisible ou droits insuffisants : fournisseur suivant */ }
        }
        return null;
    }

    /// <summary>Code Windows « Accès refusé » (NTE_PERM) : la clé existe, mais ce compte ne peut pas la lire (constaté le 2026-11-27 ;
    /// une clé inexistante donne NTE_BAD_KEYSET, 0x80090016).</summary>
    public const int AccesRefuseHResult = unchecked((int)0x80090010);

    /// <summary>Clé présente dans un fournisseur mais illisible par ce compte (droits SYSTEM et Administrateurs seulement).</summary>
    public bool AccesRefuse()
    {
        if (Ouvrir() is { } o) { o.Cle.Dispose(); return false; }
        foreach (var f in Fournisseurs)
        {
            try { CngKey.Exists(Nom, f, CngKeyOpenOptions.MachineKey); }
            catch (CryptographicException e) when (e.HResult == AccesRefuseHResult) { return true; }
            catch (CryptographicException) { /* fournisseur absent : suivant */ }
        }
        return false;
    }

    public byte[]? ClePublique()
    {
        if (Ouvrir() is not { } o) return null;
        using var cle = o.Cle;
        try { return cle.ExportSubjectPublicKeyInfo(); }
        catch (CryptographicException) { return null; }
    }

    public byte[]? Signer(byte[] donnees)
    {
        if (Ouvrir() is not { } o) return null;
        using var cle = o.Cle;
        try { return cle.SignData(donnees, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation); }
        catch (CryptographicException) { return null; }
    }

    public InfoCleServeur? Lire()
    {
        if (Ouvrir() is not { } o) return null;
        using var cle = o.Cle;
        return new InfoCleServeur(o.Fournisseur.Provider, o.Fournisseur == Tpm, EmpreinteServeur.Calculer(cle.ExportSubjectPublicKeyInfo()), false);
    }

    /// <summary>Clé ouverte par le fournisseur TPM : vrai ; fournisseur logiciel : faux ; absente : null (sans exporter la clé publique).</summary>
    public bool? Materielle()
    {
        if (Ouvrir() is not { } o) return null;
        o.Cle.Dispose();
        return o.Fournisseur == Tpm;
    }

    public InfoCleServeur CreerSiAbsente()
    {
        if (Lire() is { } existante) return existante;
        if (!EstAdministrateur())
            throw new InvalidOperationException("Droits d’administrateur nécessaires : ouvrez l’invite de commandes « en tant qu’administrateur » et relancez la commande.");
        CryptographicException? derniere = null;
        foreach (var f in Fournisseurs)
        {
            try
            {
                var p = new CngKeyCreationParameters
                {
                    Provider = f,
                    KeyCreationOptions = CngKeyCreationOptions.MachineKey,   // jamais OverwriteExistingKey : une clé existante n'est pas remplacée
                    ExportPolicy = CngExportPolicies.None,
                    KeyUsage = CngKeyUsages.Signing
                };
                var sd = new RawSecurityDescriptor(Droits);
                var octets = new byte[sd.BinaryLength];
                sd.GetBinaryForm(octets, 0);
                p.Parameters.Add(new CngProperty("Security Descr", octets, DaclSecurityInformation));
                using var cle = new ECDsaCng(CngKey.Create(CngAlgorithm.ECDsaP256, Nom, p));
                return new InfoCleServeur(f.Provider, f == Tpm, EmpreinteServeur.Calculer(cle.ExportSubjectPublicKeyInfo()), true);
            }
            catch (CryptographicException e) { derniere = e; }
        }
        throw new InvalidOperationException("Windows refuse de créer la clé de ce serveur (ni TPM ni fournisseur logiciel).", derniere);
    }

    private static bool EstAdministrateur()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }
}

/// <summary>
/// Hors Windows (pas de TPM accessible par CNG) : clé logicielle P-256 dans un fichier PKCS#8 lisible par le seul compte de l'API (droits 600).
/// Moins protégée qu'une clé TPM (le fichier se copie) : la commande de création l'écrit en avertissement.
/// </summary>
public sealed class CleServeurFichier(string fichier) : ICleServeurLocale
{
    public const string FichierNom = "cle-serveur.pem", Fournisseur = "Fichier logiciel (hors Windows)";

    private ECDsa? Ouvrir()
    {
        try
        {
            if (!File.Exists(fichier)) return null;
            var cle = ECDsa.Create();
            cle.ImportFromPem(File.ReadAllText(fichier));
            return cle;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException) { return null; }
    }

    public byte[]? ClePublique()
    {
        using var cle = Ouvrir();
        return cle?.ExportSubjectPublicKeyInfo();
    }

    public byte[]? Signer(byte[] donnees)
    {
        using var cle = Ouvrir();
        return cle?.SignData(donnees, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public InfoCleServeur? Lire() =>
        ClePublique() is { } spki ? new InfoCleServeur(Fournisseur, false, EmpreinteServeur.Calculer(spki), false) : null;

    /// <summary>Toujours logicielle (faux) ; null si le fichier est absent ou illisible.</summary>
    public bool? Materielle() => ClePublique() is null ? null : false;

    /// <summary>Fichier présent mais illisible par ce compte (droits 600 d'un autre utilisateur).</summary>
    public bool AccesRefuse()
    {
        if (!File.Exists(fichier)) return false;
        try { using var f = File.OpenRead(fichier); return false; }
        catch (UnauthorizedAccessException) { return true; }
        catch (IOException) { return false; }
    }

    public InfoCleServeur CreerSiAbsente()
    {
        if (Lire() is { } existante) return existante;
        if (File.Exists(fichier)) throw new InvalidOperationException($"Fichier de clé illisible : {fichier} (jamais remplacé ; à examiner).");
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var temporaire = fichier + ".partiel";
        File.WriteAllText(temporaire, cle.ExportPkcs8PrivateKeyPem());
        FichiersProteges.Restreindre(temporaire);
        File.Move(temporaire, fichier, overwrite: false);
        return new InfoCleServeur(Fournisseur, false, EmpreinteServeur.Calculer(cle.ExportSubjectPublicKeyInfo()), true);
    }
}

/// <summary>
/// Date plancher hors base (point 5 du 2026-11-27) : plus grande date déjà vue, dans racine\Offre\date-plancher.json (par défaut
/// C:\ProgramData\PharmaOfficine\Offre\date-plancher.json), écrit à côté puis remplacé, lisible et modifiable par SYSTEM, les Administrateurs et
/// le compte de l'API seulement. Rien de secret : effacer le fichier ne rend que la date du journal fiscal ; le reculer à la main demande un
/// compte d'administrateur. Écriture au plus une fois par jour, jamais d'un saut de plus de 2 jours (OffreCourante). Le même fichier garde la
/// dernière date reçue du serveur central (« Centrale », correction du 2026-11-27, point 4 ; au plus une fois par heure) ; un fichier
/// d'avant cette correction (sans « Centrale ») se relit tel quel.
/// </summary>
public sealed class DatePlancherFichier(string fichier, ILogger<DatePlancherFichier>? journal = null) : IDatePlancherFichier
{
    public const string FichierNom = "date-plancher.json";
    private readonly object verrou = new();

    private sealed record Contenu(DateTime? Utc, DateTime? Centrale = null);

    public static string CheminParDefaut(string racineDonnees) => Path.Combine(racineDonnees, "Offre", FichierNom);

    private static DateTime? Utc(DateTime? d) => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;

    private Contenu? LireContenu()
    {
        try
        {
            if (!File.Exists(fichier)) return null;
            return JsonSerializer.Deserialize<Contenu>(File.ReadAllText(fichier));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            journal?.LogWarning("Date plancher de l'offre illisible ({Fichier}) : seule la date du journal fiscal compte. {Erreur}", fichier, e.Message);
            return null;
        }
    }

    public DateTime? Lire()
    {
        lock (verrou) return Utc(LireContenu()?.Utc);
    }

    public DateTime? LireCentrale()
    {
        lock (verrou) return Utc(LireContenu()?.Centrale);
    }

    public void Ecrire(DateTime utc)
    {
        lock (verrou) EcrireContenu((LireContenu() ?? new Contenu(null)) with { Utc = DateTime.SpecifyKind(utc, DateTimeKind.Utc) });
    }

    public void NoterCentrale(DateTime utc)
    {
        lock (verrou) EcrireContenu((LireContenu() ?? new Contenu(null)) with { Centrale = DateTime.SpecifyKind(utc, DateTimeKind.Utc) });
    }

    private void EcrireContenu(Contenu c)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
            var temporaire = fichier + ".partiel";
            File.WriteAllText(temporaire, JsonSerializer.Serialize(c with { Utc = Utc(c.Utc), Centrale = Utc(c.Centrale) }));
            FichiersProteges.Restreindre(temporaire);
            File.Move(temporaire, fichier, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            journal?.LogWarning("Date plancher de l'offre non enregistrée ({Fichier}) : {Erreur}", fichier, e.Message);
        }
    }
}
