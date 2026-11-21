using Npgsql;
using Pharma.Application.Caisse;

namespace Pharma.Infrastructure.Caisse;

/// <summary>Réglage « facture et retour laissés » : parametre_caisse.taches_laissees_poste (script 156), ligne unique id = 1.</summary>
public sealed class ReglageTachesLaisseesSql(NpgsqlDataSource db) : IReglageTachesLaissees
{
    public async Task<string> LireAsync(CancellationToken ct = default)
    {
        // Ligne absente : valeur par défaut du schéma (chiffré)
        await using var cmd = db.CreateCommand("SELECT taches_laissees_poste FROM parametre_caisse WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as string ?? ConservationTachesLaissees.Chiffre;
    }

    public async Task EnregistrerAsync(string mode, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_caisse SET taches_laissees_poste = $1 WHERE id = 1");
        cmd.Parameters.AddWithValue(mode);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
