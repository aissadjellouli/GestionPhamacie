using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Patients;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Patients;

public sealed class PatientsSql(NpgsqlDataSource db) : IPatients, IMedecins, IOrganismes
{
    public async Task<IReadOnlyList<PatientResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT p.id, p.nom, p.prenom, p.telephone, p.num_assure, p.organisme_id, o.code, p.plafond_credit, p.solde_credit
            FROM patient p
            LEFT JOIN organisme o ON o.id = p.organisme_id
            WHERE p.nom ILIKE '%' || $1 || '%'
               OR p.prenom ILIKE '%' || $1 || '%'
               OR (p.nom || ' ' || COALESCE(p.prenom, '')) ILIKE '%' || $1 || '%'
               OR p.telephone = $1
               OR p.num_assure = $1
            ORDER BY p.nom, p.prenom
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
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

    public async Task<long> CreerAsync(NouveauPatient p, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO patient (nom, prenom, telephone, num_assure, organisme_id, plafond_credit)
            VALUES ($1, $2, $3, $4, $5, $6) RETURNING id
            """);
        cmd.Parameters.AddWithValue(p.Nom);
        cmd.Parameters.Add(Nullable(p.Prenom, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.Telephone, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.NumAssure, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nullable(p.OrganismeId, NpgsqlDbType.Bigint));
        cmd.Parameters.AddWithValue(p.PlafondCredit);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    async Task<IReadOnlyList<Medecin>> IMedecins.RechercherAsync(string texte, int limite, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom, specialite FROM medecin
            WHERE nom ILIKE '%' || $1 || '%' ORDER BY nom LIMIT $2
            """);
        cmd.Parameters.AddWithValue(texte);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Medecin>();
        while (await r.ReadAsync(ct)) liste.Add(new Medecin(r.GetInt64(0), r.GetString(1), Texte(r, 2)));
        return liste;
    }

    async Task<long> IMedecins.CreerAsync(string nom, string? specialite, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("INSERT INTO medecin (nom, specialite) VALUES ($1, $2) RETURNING id");
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.Add(Nullable(specialite, NpgsqlDbType.Text));
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<Organisme>> ListerAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, code, nom FROM organisme ORDER BY code");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Organisme>();
        while (await r.ReadAsync(ct)) liste.Add(new Organisme(r.GetInt64(0), r.GetString(1), r.GetString(2)));
        return liste;
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nullable(object? valeur, NpgsqlDbType type) =>
        new() { Value = valeur ?? DBNull.Value, NpgsqlDbType = type };
}
