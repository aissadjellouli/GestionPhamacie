using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Reprise;

namespace Pharma.Infrastructure.Reprise;

/// <summary>Doublons de n° d'assuré de la reprise (import.doublon_patient, database/migration/092_import_patients_doublons.sql).</summary>
public sealed class DoublonsImportSql(NpgsqlDataSource db) : IDoublonsImport
{
    private const string Select = """
        SELECT d.id, d.session_id, s.logiciel_source, d.ref_source, d.ligne, d.nom, d.prenom, d.telephone, d.num_assure, d.solde_credit, d.origine,
               d.patient_existant_id, btrim(p.nom || ' ' || COALESCE(p.prenom, '')), p.num_assure, p.telephone, p.solde_credit,
               d.statut, d.dette, d.patient_cree_id, u.nom_complet, d.decide_le
        FROM import.doublon_patient d
        JOIN import.session s ON s.id = d.session_id
        JOIN patient p ON p.id = d.patient_existant_id
        LEFT JOIN utilisateur u ON u.id = d.decide_par
        """;

    private const string Filtre = """
        WHERE ($1::text IS NULL OR d.statut = $1)
          AND ($2::text IS NULL OR d.nom ILIKE '%' || $2 || '%' OR COALESCE(d.prenom, '') ILIKE '%' || $2 || '%'
               OR d.num_assure ILIKE '%' || $2 || '%' OR p.nom ILIKE '%' || $2 || '%')
        """;

    public async Task<Page<DoublonPatientImport>> ListerAsync(FiltreDoublonsImport filtre, DemandePage page, CancellationToken ct = default)
    {
        int total;
        await using (var cmd = db.CreateCommand($"SELECT count(*)::int FROM import.doublon_patient d JOIN patient p ON p.id = d.patient_existant_id {Filtre}"))
        {
            Parametres(cmd, filtre);
            total = (int)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var lignes = new List<DoublonPatientImport>();
        await using (var cmd = db.CreateCommand($"{Select} {Filtre} ORDER BY d.statut <> 'a_valider', d.session_id DESC, d.ligne NULLS LAST, d.id LIMIT $3 OFFSET $4"))
        {
            Parametres(cmd, filtre);
            cmd.Parameters.AddWithValue(page.Taille);
            cmd.Parameters.AddWithValue(page.Decalage);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) lignes.Add(Lire(r));
        }
        return new Page<DoublonPatientImport>(lignes, total, page.Page, page.Taille);
    }

    public async Task<DoublonPatientImport?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"{Select} WHERE d.id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<long> DeciderAsync(long id, DecisionDoublon decision, DetteDoublon? dette, string? numAssure, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT import.decider_doublon_patient($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(DeciderDoublonImport.Code(decision));
        cmd.Parameters.Add(new NpgsqlParameter { Value = dette switch { DetteDoublon.Rattacher => "rattachee", DetteDoublon.Abandonner => "abandonnee", _ => DBNull.Value }, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)numAssure ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void Parametres(NpgsqlCommand cmd, FiltreDoublonsImport f)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Statut ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        // Caractères spéciaux du ILIKE neutralisés : la recherche est un texte littéral
        var q = f.Recherche?.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)q ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    private static DoublonPatientImport Lire(NpgsqlDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3), r.IsDBNull(4) ? null : r.GetInt32(4),
        r.GetString(5), T(r, 6), T(r, 7), r.GetString(8), r.GetDecimal(9), r.GetString(10),
        r.GetInt64(11), r.GetString(12), T(r, 13), T(r, 14), r.GetDecimal(15),
        r.GetString(16), T(r, 17), r.IsDBNull(18) ? null : r.GetInt64(18), T(r, 19), r.IsDBNull(20) ? null : r.GetDateTime(20));

    private static string? T(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
