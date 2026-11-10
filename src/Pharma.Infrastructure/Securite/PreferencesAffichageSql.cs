using Npgsql;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>Colonne utilisateur.affichage (025_affichage.sql).</summary>
public sealed class PreferencesAffichageSql(NpgsqlDataSource db) : IPreferencesAffichage
{
    public async Task<string> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT affichage FROM utilisateur WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteScalarAsync(ct) as string ?? "auto";
    }

    public async Task EnregistrerAsync(long utilisateurId, string affichage, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE utilisateur SET affichage = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(affichage);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
