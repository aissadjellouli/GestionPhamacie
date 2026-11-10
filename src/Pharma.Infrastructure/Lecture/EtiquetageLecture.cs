using Npgsql;
using Pharma.Application.Tracabilite;

namespace Pharma.Infrastructure.Lecture;

public sealed class EtiquetageLecture(NpgsqlDataSource db) : IEtiquetageLecture
{
    public async Task<EtatEtiquetage> EtatAsync(CancellationToken ct = default)
    {
        const string sql = """
            SELECT (SELECT count(*) FROM boite WHERE statut = 'en_stock')::int,
                   (SELECT COALESCE(SUM(quantite_disponible), 0) FROM lot WHERE quantite_disponible > 0)::int,
                   (SELECT boite_obligatoire FROM parametre_tracabilite WHERE id = 1)
            """;
        await using var cmd = db.CreateCommand(sql);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new EtatEtiquetage(r.GetInt32(0), r.GetInt32(1), !r.IsDBNull(2) && r.GetBoolean(2));
    }
}
