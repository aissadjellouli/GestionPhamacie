using System.Security.Cryptography;
using System.Text;

namespace Pharma.Infrastructure.Securite;

/// <summary>
/// Protection des secrets rangés hors de la base sur le serveur (clé de scellement, compte super admin, clé de synchronisation,
/// mot de passe SMTP). Sous Windows : DPAPI en portée machine, inchangé (mêmes fichiers, même entropie : les données déjà protégées
/// se relisent comme avant). Hors Windows : ASP.NET Core DataProtection, clés dans un dossier réglable (volume persistant) ;
/// cette implémentation est fournie par l'hôte (Pharma.Api.Securite.ProtectionDataProtection : l'Infrastructure ignore ASP.NET Core).
/// Une donnée illisible lève <see cref="CryptographicException"/>, comme DPAPI.
/// </summary>
public interface IProtectionDonnees
{
    /// <summary>Mode retenu, pour le journal du serveur (aucun secret) : « DPAPI machine » ou « DataProtection (dossier …) ».</summary>
    string Description { get; }

    /// <param name="usage">Sépare les usages : sous DPAPI, c'est l'entropie (octets UTF-8), identique à celle d'avant.</param>
    byte[] Proteger(byte[] clair, string usage);

    byte[] Deproteger(byte[] protege, string usage);
}

/// <summary>Réglages de la section « Protection » (appsettings ou variables Protection__Mode, Protection__DossierCles…).</summary>
/// <param name="Mode">« auto » (défaut : DPAPI sous Windows, DataProtection ailleurs), « dpapi » (Windows seulement) ou « dataprotection ».</param>
/// <param name="DossierCles">Dossier des clés DataProtection (vide : « cles-protection » sous la racine des données).</param>
/// <param name="CertificatFichier">Certificat PFX qui chiffre les clés DataProtection au repos (facultatif, recommandé).</param>
/// <param name="CertificatMotDePasse">Mot de passe du PFX : variable d'environnement ou coffre, jamais dans un fichier du dépôt.</param>
public sealed record ReglagesProtection(string? Mode = null, string? DossierCles = null, string? CertificatFichier = null, string? CertificatMotDePasse = null);

/// <summary>Choix de l'implémentation de protection selon la plateforme et le réglage Protection:Mode.</summary>
public static class ChoixProtection
{
    public const string Dpapi = "dpapi";
    public const string DataProtection = "dataprotection";
    public const string Auto = "auto";
    public const string SousDossierCles = "cles-protection";

    /// <summary>Mode effectif : sans réglage (ou « auto »), DPAPI sous Windows et DataProtection ailleurs ; un réglage explicite l'emporte.</summary>
    public static string Mode(string? reglage, bool windows) => reglage?.Trim().ToLowerInvariant() switch
    {
        null or "" or Auto => windows ? Dpapi : DataProtection,
        Dpapi when windows => Dpapi,
        Dpapi => throw new InvalidOperationException("Protection:Mode « dpapi » : Windows seulement. Retirez le réglage ou choisissez « dataprotection »."),
        DataProtection => DataProtection,
        var autre => throw new InvalidOperationException($"Protection:Mode « {autre} » inconnu : « auto », « dpapi » ou « dataprotection ».")
    };

    /// <summary>Dossier des clés DataProtection : réglage Protection:DossierCles, sinon « cles-protection » sous la racine des données.</summary>
    public static string DossierCles(ReglagesProtection reglages, string racineDonnees) =>
        string.IsNullOrWhiteSpace(reglages.DossierCles) ? Path.Combine(racineDonnees, SousDossierCles) : reglages.DossierCles;
}

/// <summary>DPAPI en portée machine (Windows) : comportement d'origine des fichiers .dat et du mot de passe SMTP.</summary>
public sealed class ProtectionDpapiMachine : IProtectionDonnees
{
    public string Description => "DPAPI machine";

    public byte[] Proteger(byte[] clair, string usage)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI : Windows seulement.");
        return ProtectedData.Protect(clair, Encoding.UTF8.GetBytes(usage), DataProtectionScope.LocalMachine);
    }

    public byte[] Deproteger(byte[] protege, string usage)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("DPAPI : Windows seulement.");
        return ProtectedData.Unprotect(protege, Encoding.UTF8.GetBytes(usage), DataProtectionScope.LocalMachine);
    }
}

/// <summary>Droits des fichiers de secrets : ACL Windows d'origine sous Windows ; ailleurs, lecture et écriture par le seul compte de l'API.</summary>
public static class FichiersProteges
{
    public static void Restreindre(string fichier)
    {
        if (OperatingSystem.IsWindows()) CompteSuperAdminFichier.Restreindre(fichier);
        else File.SetUnixFileMode(fichier, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public static void RestreindreDossier(string dossier)
    {
        if (OperatingSystem.IsWindows()) Synchronisation.CertificatSyncFichier.RestreindreDossier(dossier);
        else File.SetUnixFileMode(dossier, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>Dossier monté par l'exploitant (volume) dont l'API n'est pas propriétaire : ses droits restent ceux du montage.</summary>
    public static void RestreindreDossierSiPossible(string dossier)
    {
        try { RestreindreDossier(dossier); }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { }
    }
}
