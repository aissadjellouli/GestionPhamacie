using Npgsql;
using Pharma.Application.Reglages;

namespace Pharma.Infrastructure.Reglages;

/// <summary>Lecture et écriture de parametre_fiscal et parametre_tracabilite (lignes uniques, id = 1). La garde zz_reglage_garde (script 141) reste le dernier rempart.</summary>
public sealed class ParametresFiscalTracabiliteSql(NpgsqlDataSource db) : IParametresFiscalTracabilite
{
    public async Task<ParametreFiscal> FiscalAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT conservation_annees, format_export_dgi, separateur_csv, fuseau FROM parametre_fiscal WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new ParametreFiscal(r.GetInt16(0), r.GetString(1), r.GetString(2), r.GetString(3))
            : new ParametreFiscal(6, "csv-v1", ";", "Africa/Algiers");
    }

    public async Task EnregistrerFiscalAsync(ParametreFiscal p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_fiscal (id, conservation_annees, format_export_dgi, separateur_csv, fuseau) VALUES (1, $1, $2, $3, $4)
            ON CONFLICT (id) DO UPDATE SET conservation_annees = EXCLUDED.conservation_annees, format_export_dgi = EXCLUDED.format_export_dgi,
                separateur_csv = EXCLUDED.separateur_csv, fuseau = EXCLUDED.fuseau
            """);
        cmd.Parameters.AddWithValue((short)p.ConservationAnnees);
        cmd.Parameters.AddWithValue(p.FormatExportDgi);
        cmd.Parameters.AddWithValue(p.SeparateurCsv);
        cmd.Parameters.AddWithValue(p.Fuseau);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<ParametreTracabilite> TracabiliteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT boite_obligatoire, patient_obligatoire FROM parametre_tracabilite WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ParametreTracabilite(r.GetBoolean(0), r.GetString(1)) : new ParametreTracabilite(false, "ordonnance");
    }

    public async Task EnregistrerTracabiliteAsync(ParametreTracabilite p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_tracabilite (id, boite_obligatoire, patient_obligatoire) VALUES (1, $1, $2)
            ON CONFLICT (id) DO UPDATE SET boite_obligatoire = EXCLUDED.boite_obligatoire, patient_obligatoire = EXCLUDED.patient_obligatoire
            """);
        cmd.Parameters.AddWithValue(p.BoiteObligatoire);
        cmd.Parameters.AddWithValue(p.PatientObligatoire);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> FuseauConnuAsync(string fuseau, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_timezone_names WHERE name = $1)");
        cmd.Parameters.AddWithValue(fuseau);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }
}
