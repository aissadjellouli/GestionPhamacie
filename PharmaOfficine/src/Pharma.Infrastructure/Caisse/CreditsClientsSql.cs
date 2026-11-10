using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Crédits clients : patient.solde_credit. Un remboursement est un paiement sans vente (vente_id NULL, patient_id)
/// rattaché à la caisse du poste : les espèces comptent dans l'attendu de clôture.
/// </summary>
public sealed class CreditsClientsSql(NpgsqlDataSource db) : ICreditsClients
{
    private const string Select = """
        SELECT p.id, trim(concat_ws(' ', p.nom, p.prenom)), p.telephone, p.solde_credit, p.plafond_credit,
               (SELECT max(v.date_vente) FROM vente v JOIN paiement x ON x.vente_id = v.id WHERE v.patient_id = p.id AND x.mode = 'credit' AND v.statut = 'validee')
        FROM patient p
        """;

    public async Task<IReadOnlyList<PatientEnCredit>> AvecCreditAsync(string? recherche, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            {Select}
            WHERE p.solde_credit > 0 AND ($1::text IS NULL OR concat_ws(' ', p.nom, p.prenom) ILIKE '%' || $1 || '%' OR p.telephone LIKE $1 || '%')
            ORDER BY p.solde_credit DESC LIMIT 50
            """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await Lire(cmd, ct);
    }

    public async Task<SituationCredit?> SituationAsync(long patientId, CancellationToken ct = default)
    {
        PatientEnCredit? patient;
        await using (var cmd = db.CreateCommand($"{Select} WHERE p.id = $1"))
        {
            cmd.Parameters.AddWithValue(patientId);
            patient = (await Lire(cmd, ct)).FirstOrDefault();
        }
        if (patient is null) return null;

        var ventes = new List<VenteACredit>();
        await using (var cmd = db.CreateCommand("""
            SELECT v.numero, v.date_vente, x.montant FROM vente v JOIN paiement x ON x.vente_id = v.id
            WHERE v.patient_id = $1 AND x.mode = 'credit' AND v.statut = 'validee' ORDER BY v.date_vente DESC LIMIT 10
            """))
        {
            cmd.Parameters.AddWithValue(patientId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) ventes.Add(new VenteACredit(r.GetString(0), r.GetDateTime(1), r.GetDecimal(2)));
        }
        return new SituationCredit(patient, ventes);
    }

    public async Task<long> EncaisserAsync(long patientId, decimal montant, string mode, long sessionCaisseId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Dette diminuée seulement si elle couvre encore le montant (deux postes ne peuvent pas encaisser deux fois)
        await using (var cmd = new NpgsqlCommand(
            "UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx))
        {
            cmd.Parameters.AddWithValue(patientId);
            cmd.Parameters.AddWithValue(montant);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La dette du patient vient de changer : rechargez sa situation.");
        }
        long id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO paiement (patient_id, mode, montant, utilisateur_id, session_caisse_id) VALUES ($1, $2, $3, $4, $5) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(patientId);
            cmd.Parameters.AddWithValue(mode);
            cmd.Parameters.AddWithValue(montant);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(sessionCaisseId);
            id = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return id;
    }

    private static async Task<IReadOnlyList<PatientEnCredit>> Lire(NpgsqlCommand cmd, CancellationToken ct)
    {
        var liste = new List<PatientEnCredit>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new PatientEnCredit(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDecimal(3), r.GetDecimal(4),
                r.IsDBNull(5) ? null : r.GetDateTime(5)));
        return liste;
    }
}
