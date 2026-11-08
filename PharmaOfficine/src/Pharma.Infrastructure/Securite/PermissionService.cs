using Npgsql;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>Vérifie un droit côté serveur avec la fonction SQL a_permission.</summary>
public sealed class PermissionService(NpgsqlDataSource db) : IPermissionService
{
    public async Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT a_permission($1, $2)");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(permission);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT rp.permission_code
            FROM utilisateur u JOIN role_permission rp ON rp.role_id = u.role_id
            WHERE u.id = $1 AND u.actif
            ORDER BY 1
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
            "SELECT r.remise_max_pct FROM utilisateur u JOIN role r ON r.id = u.role_id WHERE u.id = $1 AND u.actif");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) is decimal d ? d : 0m;
    }

    public async Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT r.systeme FROM utilisateur u JOIN role r ON r.id = u.role_id WHERE u.id = $1 AND u.actif");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
