using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Patients;

namespace Pharma.Infrastructure.Patients;

/// <summary>
/// Patients › « Droits expirés ou bientôt expirés » (2026-11-15) : patients CNAS/CASNOS dont la date de fin de droits est dépassée
/// (avant aujourd'hui) ou dans les N jours ; la plus proche (ou la plus ancienne) d'abord ; dernière vente du patient (idx_vente_patient).
/// </summary>
public sealed class SuiviDroitsChifaSql(NpgsqlDataSource db) : ISuiviDroitsChifa
{
    private const string Filtre = """
        FROM patient p
        JOIN organisme o ON o.id = p.organisme_id AND o.code IN ('CNAS', 'CASNOS')
        WHERE p.date_fin_droit IS NOT NULL
          AND p.date_fin_droit <= $1::date + $2::int
          AND ($3::text IS NULL OR ($3 = 'expires' AND p.date_fin_droit < $1::date) OR ($3 = 'bientot' AND p.date_fin_droit >= $1::date))
          AND ($4::text IS NULL OR o.code = $4)
          AND ($5::date IS NULL OR p.date_fin_droit >= $5)
          AND ($6::date IS NULL OR p.date_fin_droit <= $6)
        """;

    public async Task<Page<SuiviDroitChifa>> PageAsync(FiltreSuiviDroits f, DemandePage page, CancellationToken ct = default)
    {
        await using var compte = db.CreateCommand("SELECT count(*) " + Filtre);
        Parametres(compte, f);
        var total = Convert.ToInt32(await compte.ExecuteScalarAsync(ct));

        await using var cmd = db.CreateCommand($"""
            SELECT p.id, trim(p.nom || ' ' || COALESCE(p.prenom, '')), p.num_assure, o.code, p.date_fin_droit,
                   (SELECT max(v.date_vente) FROM vente v WHERE v.patient_id = p.id)
            {Filtre}
            ORDER BY p.date_fin_droit, p.nom, p.id
            OFFSET $7 LIMIT $8
            """);
        Parametres(cmd, f);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.AddWithValue(page.Taille);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lignes = new List<SuiviDroitChifa>();
        while (await r.ReadAsync(ct))
            lignes.Add(new SuiviDroitChifa(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3),
                r.GetFieldValue<DateOnly>(4), r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return new Page<SuiviDroitChifa>(lignes, total, page.Page, page.Taille);
    }

    private static void Parametres(NpgsqlCommand cmd, FiltreSuiviDroits f)
    {
        cmd.Parameters.AddWithValue(f.Aujourdhui);
        cmd.Parameters.AddWithValue(Math.Max(0, f.JoursAlerte));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Etat ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Organisme ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Du ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Au ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
    }
}
