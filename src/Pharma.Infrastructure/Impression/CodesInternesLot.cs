using Npgsql;

namespace Pharma.Infrastructure.Impression;

/// <summary>Code interne EAN-13 d'un lot, créé au premier appel (fonction SQL generer_code_interne).</summary>
public sealed class CodesInternesLot(NpgsqlDataSource dataSource)
{
    public async Task<string> ObtenirAsync(long lotId, CancellationToken ct = default)
    {
        await using var cmd = dataSource.CreateCommand("SELECT generer_code_interne($1)");
        cmd.Parameters.AddWithValue(lotId);
        return (string)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
