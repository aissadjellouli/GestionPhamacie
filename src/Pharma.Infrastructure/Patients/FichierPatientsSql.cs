using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Patients;

/// <summary>
/// Patients › « Fichier des patients » (2026-11-18) : liste filtrée et paginée, par nom puis prénom. Texte : nom, prénom, « nom prénom »,
/// n° d'assuré (texte, ou chiffres seuls par début dès 4 chiffres) et téléphone (même règle que la recherche au comptoir).
/// Droits Chifa : même définition que la liste de suivi (organisme CNAS/CASNOS, date dépassée ou dans les N jours).
/// </summary>
public sealed class FichierPatientsSql(NpgsqlDataSource db) : IFichierPatients
{
    private static readonly string Filtre = $"""
        FROM patient p
        LEFT JOIN organisme o ON o.id = p.organisme_id
        WHERE ($1::text IS NULL
               OR p.nom ILIKE '%' || $1 || '%'
               OR p.prenom ILIKE '%' || $1 || '%'
               OR (p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
               OR p.num_assure ILIKE '%' || $1 || '%'
               OR ($2::text IS NOT NULL AND {TelephoneSql.Normalise("p.telephone")} LIKE $2 || '%')
               OR ($11::text IS NOT NULL AND p.num_assure IS NOT NULL AND regexp_replace(p.num_assure, '\D', '', 'g') LIKE $11 || '%'))
          AND ($3::bigint IS NULL OR p.organisme_id = $3)
          AND (NOT $4::boolean OR p.organisme_id IS NULL)
          AND ($5::boolean IS NULL OR p.est_chronique = $5)
          AND ($6::boolean IS NULL OR (p.solde_credit > 0) = $6)
          AND ($7::text IS NULL OR (o.code IN ('CNAS', 'CASNOS') AND p.date_fin_droit IS NOT NULL
               AND p.date_fin_droit <= $8::date + $9::int
               AND ($7 = 'expires_ou_bientot' OR ($7 = 'expires' AND p.date_fin_droit < $8::date) OR ($7 = 'bientot' AND p.date_fin_droit >= $8::date))))
          AND ($10::boolean IS NULL OR p.consentement_sms = $10)
        """;

    public async Task<Page<LigneFichierPatient>> PageAsync(FiltreFichierPatients f, DemandePage page, CancellationToken ct = default)
    {
        await using var compte = db.CreateCommand("SELECT count(*) " + Filtre);
        Parametres(compte, f);
        var total = Convert.ToInt32(await compte.ExecuteScalarAsync(ct));

        await using var cmd = db.CreateCommand($"""
            SELECT p.id, p.nom, p.prenom, p.telephone, p.num_assure, o.code, p.date_fin_droit, p.est_chronique, p.consentement_sms, p.solde_credit
            {Filtre}
            ORDER BY lower(p.nom), lower(COALESCE(p.prenom, '')), p.id
            OFFSET $12 LIMIT $13
            """);
        Parametres(cmd, f);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.AddWithValue(page.Taille);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var lignes = new List<LigneFichierPatient>();
        while (await r.ReadAsync(ct))
            lignes.Add(new LigneFichierPatient(r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), Texte(r, 4), Texte(r, 5),
                r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetBoolean(7), r.GetBoolean(8), r.GetDecimal(9)));
        return new Page<LigneFichierPatient>(lignes, total, page.Page, page.Taille);
    }

    /// <summary>Texte cherché avec ILIKE : %, _ et \ saisis sont pris à la lettre.</summary>
    internal static string? Motif(string? texte) =>
        texte is null ? null : texte.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");

    /// <summary>Chiffres du texte (n° d'assuré saisi avec ou sans séparateur), dès 4 chiffres ; sinon null.</summary>
    internal static string? ChiffresAssure(string? texte)
    {
        if (texte is null) return null;
        var c = new string(texte.Where(char.IsAsciiDigit).ToArray());
        return c.Length >= 4 ? c : null;
    }

    private static void Parametres(NpgsqlCommand cmd, FiltreFichierPatients f)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)Motif(f.Texte) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)(f.Texte is null ? null : RechercheTelephone.Prefixe(f.Texte)) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.OrganismeId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = f.SansOrganisme, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Chronique ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.ACredit ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Droits ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = f.Aujourdhui, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = Math.Max(0, f.JoursAlerte), NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.AccordSms ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Boolean });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ChiffresAssure(f.Texte) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
}
