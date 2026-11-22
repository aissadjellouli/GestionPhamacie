using Npgsql;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>
/// Vérifie un droit côté serveur avec la fonction SQL a_permission. Service créé pour chaque requête HTTP :
/// une réponse est gardée le temps de la requête (le filtre de l'API puis le cas d'usage vérifient le même droit).
/// </summary>
public sealed class PermissionService(NpgsqlDataSource db) : IPermissionService
{
    private readonly Dictionary<(long, string), bool> dejaVerifies = [];

    public async Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default)
    {
        if (dejaVerifies.TryGetValue((utilisateurId, permission), out var connu)) return connu;
        await using var cmd = db.CreateCommand("SELECT a_permission($1, $2)");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(permission);
        var autorise = await cmd.ExecuteScalarAsync(ct) is true;
        dejaVerifies[(utilisateurId, permission)] = autorise;
        return autorise;
    }

    public async Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.code FROM permission p WHERE a_permission($1, p.code) ORDER BY 1
            """);
        cmd.Parameters.AddWithValue(utilisateurId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var codes = new List<string>();
        while (await r.ReadAsync(ct)) codes.Add(r.GetString(0));
        return codes;
    }

    public async Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT CASE WHEN r.systeme THEN 100 ELSE u.remise_max_pct END FROM utilisateur u JOIN role r ON r.id = u.role_id WHERE u.id = $1 AND u.actif");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) is decimal d ? d : 0m;
    }

    public async Task<string> LibelleAsync(string permission, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT libelle FROM permission WHERE code = $1");
        cmd.Parameters.AddWithValue(permission);
        return await cmd.ExecuteScalarAsync(ct) is string { Length: > 0 } libelle ? libelle : permission;
    }

    public async Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT r.systeme FROM utilisateur u JOIN role r ON r.id = u.role_id WHERE u.id = $1 AND u.actif");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
