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
/// Hors Windows : chiffré par DataProtection (IProtectionDonnees), fichier en lecture et écriture pour le seul compte de l'API.
/// </summary>
public sealed class CompteSuperAdminFichier(string chemin, IProtectionDonnees protection) : ICompteSuperAdmin
{
    private const string Usage = "PharmaOfficine.SuperAdmin";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object verrou = new();

    public string Emplacement => chemin;

    public CompteSuperAdmin? Lire()
    {
        lock (verrou)
        {
            if (!File.Exists(chemin)) return null;
            try
            {
                var clair = protection.Deproteger(File.ReadAllBytes(chemin), Usage);
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
        var chiffre = protection.Proteger(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(compte, Json)), Usage);
        lock (verrou)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
            // Écrit à côté puis remplace : jamais de fichier à moitié écrit
            var temporaire = chemin + ".partiel";
            File.WriteAllBytes(temporaire, chiffre);
            FichiersProteges.Restreindre(temporaire);
            File.Move(temporaire, chemin, overwrite: true);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static void Restreindre(string fichier)
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
