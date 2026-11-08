using Npgsql;
using Pharma.Application.Alertes;

namespace Pharma.Infrastructure.Alertes;

/// <summary>Délègue le calcul à la fonction SQL generer_alertes (database/002_alertes_stock.sql).</summary>
public sealed class MoteurAlertesSql(NpgsqlDataSource db) : IMoteurAlertes
{
    public async Task<int> GenererAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT generer_alertes()");
        return await cmd.ExecuteScalarAsync(ct) is int n ? n : 0;
    }
}
