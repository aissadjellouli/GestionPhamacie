using Npgsql;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

public sealed class DetenteursDroitSql(NpgsqlDataSource db) : IDetenteursDroit
{
    public async Task<IReadOnlyList<DetenteurDroit>> ListerAsync(string permission, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT nom_complet, pin_hash IS NOT NULL FROM utilisateur WHERE actif AND a_permission(id, $1) ORDER BY nom_complet");
        cmd.Parameters.AddWithValue(permission);
        var liste = new List<DetenteurDroit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) liste.Add(new DetenteurDroit(r.GetString(0), r.GetBoolean(1)));
        return liste;
    }
}
