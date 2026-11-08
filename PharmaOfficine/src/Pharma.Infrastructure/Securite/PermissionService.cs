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
}
