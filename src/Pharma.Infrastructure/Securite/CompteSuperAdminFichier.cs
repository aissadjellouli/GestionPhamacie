using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Npgsql;
using Pharma.Application.SuperAdmin;

namespace Pharma.Infrastructure.Securite;

/// <summary>
/// Compte du super admin dans un fichier du PC serveur (par défaut C:\ProgramData\PharmaOfficine\super-admin.dat), chiffré par
/// DPAPI en portée machine : illisible sur un autre PC, et indépendant de la base (il sert aussi quand elle est détruite).
/// Le fichier n'est lisible que par le système, les administrateurs et le compte qui l'a créé.
/// </summary>
public sealed class CompteSuperAdminFichier(string chemin) : ICompteSuperAdmin
{
    private static readonly byte[] Entropie = "PharmaOfficine.SuperAdmin"u8.ToArray();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object verrou = new();

    public string Emplacement => chemin;

    public CompteSuperAdmin? Lire()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Compte super admin : Windows seulement (DPAPI).");
        lock (verrou)
        {
            if (!File.Exists(chemin)) return null;
            try
            {
                var clair = ProtectedData.Unprotect(File.ReadAllBytes(chemin), Entropie, DataProtectionScope.LocalMachine);
                return JsonSerializer.Deserialize<CompteSuperAdmin>(Encoding.UTF8.GetString(clair), Json);
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException($"Compte super admin illisible sur ce PC ({chemin}) : recréez-le avec Pharma.Api.exe --super-admin.");
            }
        }
    }

    public void Enregistrer(CompteSuperAdmin compte)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Compte super admin : Windows seulement (DPAPI).");
        var chiffre = ProtectedData.Protect(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(compte, Json)), Entropie, DataProtectionScope.LocalMachine);
        lock (verrou)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            // Écrit à côté puis remplace : jamais de fichier à moitié écrit
            var temporaire = chemin + ".partiel";
            File.WriteAllBytes(temporaire, chiffre);
            Restreindre(temporaire);
            File.Move(temporaire, chemin, overwrite: true);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void Restreindre(string fichier)
    {
        var droits = new FileSecurity();
        droits.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var compte in new IdentityReference[]
                 {
                     new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                     new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                     WindowsIdentity.GetCurrent().User!
                 }.Distinct())
            droits.AddAccessRule(new FileSystemAccessRule(compte, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(fichier).SetAccessControl(droits);
    }
}

/// <summary>Pharmacien titulaire actif (rôle système, comme PermissionService.EstTitulaireAsync) : auteur des données chargées par le super admin.</summary>
public sealed class TitulaireLecture(NpgsqlDataSource db) : ITitulaireLecture
{
    public async Task<long?> IdAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT u.id FROM utilisateur u JOIN role r ON r.id = u.role_id
            WHERE r.systeme AND u.actif ORDER BY u.id LIMIT 1
            """);
        return await cmd.ExecuteScalarAsync(ct) as long?;
    }
}
