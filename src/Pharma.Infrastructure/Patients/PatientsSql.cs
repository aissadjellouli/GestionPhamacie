using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Patients;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Patients;

public sealed class PatientsSql(NpgsqlDataSource db) : IPatients, IMedecins, IOrganismes
{
    public async Task<IReadOnlyList<PatientResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default)
    {
        // Téléphone : par début de numéro, à partir de 4 chiffres, séparateurs ignorés, +213 = 0 (même règle que les crédits clients)
        await using var cmd = db.CreateCommand($"""
            SELECT p.id, p.nom, p.prenom, p.telephone, p.num_assure, p.organisme_id, o.code, p.plafond_credit, p.solde_credit
            FROM patient p
            LEFT JOIN organisme o ON o.id = p.organisme_id
            WHERE p.nom ILIKE '%' || $1 || '%'
               OR p.prenom ILIKE '%' || $1 || '%'
               OR (p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
               OR ($3::text IS NOT NULL AND {TelephoneSql.Normalise("p.telephone")} LIKE $3 || '%')
               OR p.num_assure = $1
            ORDER BY p.nom, p.prenom
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)RechercheTelephone.Prefixe(texte) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<PatientResume>();
        while (await r.ReadAsync(ct))
            liste.Add(new PatientResume(r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), Texte(r, 4),
                r.IsDBNull(5) ? null : r.GetInt64(5), Texte(r, 6), r.GetDecimal(7), r.GetDecimal(8)));
        return liste;
    }

    public async Task<Patient?> TrouverAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom, prenom, telephone, num_assure, organisme_id, plafond_credit, solde_credit
            FROM patient WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Patient
        {
            Id = r.GetInt64(0), Nom = r.GetString(1), Prenom = Texte(r, 2), Telephone = Texte(r, 3),
            NumAssure = Texte(r, 4), OrganismeId = r.IsDBNull(5) ? null : r.GetInt64(5),
            PlafondCredit = r.GetDecimal(6), SoldeCredit = r.GetDecimal(7)
        };
    }

    public async Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom, prenom, date_naissance, telephone, email, num_assure, organisme_id, est_chronique, plafond_credit, solde_credit
            FROM patient WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new FichePatient(r.GetInt64(0), r.GetString(1), Texte(r, 2), r.IsDBNull(3) ? null : r.GetFieldValue<DateOnly>(3), Texte(r, 4),
            Texte(r, 5), Texte(r, 6), r.IsDBNull(7) ? null : r.GetInt64(7), r.GetBoolean(8), r.GetDecimal(9), r.GetDecimal(10));
    }

    public async Task<bool> ModifierAsync(FichePatient f, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE patient SET nom = $2, prenom = $3, date_naissance = $4, telephone = $5, email = $6, num_assure = $7,
                               organisme_id = $8, est_chronique = $9, plafond_credit = $10
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(f.Id);
        cmd.Parameters.AddWithValue(f.Nom);
        cmd.Parameters.Add(Nullable(f.Prenom, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(f.DateNaissance, NpgsqlDbType.Date));
        cmd.Parameters.Add(Nullable(f.Telephone, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(f.Email, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(f.NumAssure, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(f.OrganismeId, NpgsqlDbType.Bigint));
        cmd.Parameters.AddWithValue(f.Chronique);
        cmd.Parameters.AddWithValue(f.PlafondCredit);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    async Task<bool> IMedecins.ModifierAsync(Medecin m, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE medecin SET nom = $2, specialite = $3, telephone = $4, adresse = $5, email = $6, numero_ordre = $7, code_prestataire = $8 WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(m.Id);
        ParametresMedecin(cmd, m);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    /// <summary>Nom, spécialité, téléphone, adresse, e-mail, n° d'ordre et code CNAS (code_prestataire), dans cet ordre, à la suite des paramètres déjà posés.</summary>
    private static void ParametresMedecin(NpgsqlCommand cmd, Medecin m)
    {
        cmd.Parameters.AddWithValue(m.Nom);
        cmd.Parameters.Add(Nullable(m.Specialite, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(m.Telephone, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(m.Adresse, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(m.Email, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(m.NumeroOrdre, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(m.CodeCnas, NpgsqlDbType.Text));
    }

    public async Task<long> CreerAsync(NouveauPatient p, long? creePar, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO patient (nom, prenom, telephone, num_assure, organisme_id, plafond_credit, email, cree_par)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING id
            """);
        cmd.Parameters.AddWithValue(p.Nom);
        cmd.Parameters.Add(Nullable(p.Prenom, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.Telephone, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.NumAssure, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.OrganismeId, NpgsqlDbType.Bigint));
        cmd.Parameters.AddWithValue(p.PlafondCredit);
        cmd.Parameters.Add(Nullable(p.Email, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(creePar, NpgsqlDbType.Bigint));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    async Task<IReadOnlyList<Medecin>> IMedecins.RechercherAsync(string texte, int limite, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom, specialite, telephone, adresse, email, numero_ordre, code_prestataire FROM medecin
            WHERE nom ILIKE '%' || $1 || '%' ORDER BY nom LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Medecin>();
        while (await r.ReadAsync(ct))
            liste.Add(new Medecin(r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), Texte(r, 4), Texte(r, 5), Texte(r, 6), Texte(r, 7)));
        return liste;
    }

    async Task<long> IMedecins.CreerAsync(Medecin m, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO medecin (nom, specialite, telephone, adresse, email, numero_ordre, code_prestataire) VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        ParametresMedecin(cmd, m);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<Organisme>> ListerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, code, nom, delai_paiement_jours FROM organisme WHERE actif ORDER BY code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Organisme>();
        while (await r.ReadAsync(ct)) liste.Add(new Organisme(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3)));
        return liste;
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nullable(object? valeur, NpgsqlDbType type) =>
        new() { Value = valeur ?? DBNull.Value, NpgsqlDbType = type };
}
