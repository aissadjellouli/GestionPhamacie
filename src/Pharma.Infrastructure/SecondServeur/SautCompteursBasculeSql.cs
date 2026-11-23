using Npgsql;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>Fonction bascule_saut_promotion (script 164) : une transaction, une seule fois par timeline, verrou consultatif contre deux démarrages simultanés.</summary>
public sealed class SautCompteursBasculeSql(NpgsqlDataSource db) : ISautCompteursBascule
{
    public async Task<ResultatSautCompteurs> AppliquerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT timeline_notee, saut_fait, compteurs_sautes FROM bascule_saut_promotion()");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new ResultatSautCompteurs(r.GetInt32(0), r.GetBoolean(1), r.GetInt32(2));
    }
}
