using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Caisse;
using Pharma.Domain.Ventes;

namespace Pharma.Infrastructure.Caisse;

/// <summary>
/// Écriture de l'avoir d'un patient (106_avoir_patient.sql) dans une transaction existante : patient verrouillé, solde relu,
/// jamais négatif (deux caisses ne consomment pas le même avoir ; le trigger de la table le garantit aussi).
/// </summary>
internal static class EcritureAvoir
{
    public static async Task<decimal> VerrouillerSoldeAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patientId, CancellationToken ct)
    {
        await using (var verrou = new NpgsqlCommand("SELECT 1 FROM patient WHERE id = $1 FOR UPDATE", cx, tx))
        {
            verrou.Parameters.AddWithValue(patientId);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        await using var cmd = new NpgsqlCommand("SELECT COALESCE((SELECT solde FROM v_avoir_patient WHERE patient_id = $1), 0)", cx, tx);
        cmd.Parameters.AddWithValue(patientId);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public static async Task CrediterAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patientId, decimal montant, string origine, long? venteId,
        string? motif, long utilisateurId, CancellationToken ct)
    {
        if (montant <= 0) return;
        await VerrouillerSoldeAsync(cx, tx, patientId, ct);
        await InsererAsync(cx, tx, patientId, RegleAvoir.Credit, montant, origine, venteId, null, null, motif, utilisateurId, ct);
    }

    /// <summary>Débit sous verrou : refus « Avoir insuffisant (solde : x DA) » si le solde relu ne couvre pas le montant.</summary>
    public static async Task DebiterAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patientId, decimal montant, string origine, long? venteId,
        long? paiementId, long? mouvementTiroirId, string? motif, long utilisateurId, CancellationToken ct)
    {
        var solde = await VerrouillerSoldeAsync(cx, tx, patientId, ct);
        if (solde < montant) throw RegleAvoir.Insuffisant(solde);
        await InsererAsync(cx, tx, patientId, RegleAvoir.Debit, montant, origine, venteId, paiementId, mouvementTiroirId, motif, utilisateurId, ct);
    }

    private static async Task InsererAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long patientId, string sens, decimal montant, string origine,
        long? venteId, long? paiementId, long? mouvementTiroirId, string? motif, long utilisateurId, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO avoir_mouvement (patient_id, sens, montant, origine, vente_id, paiement_id, mouvement_tiroir_id, motif, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, cx, tx);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(sens);
        cmd.Parameters.AddWithValue(montant);
        cmd.Parameters.AddWithValue(origine);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)venteId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)paiementId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)mouvementTiroirId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)motif ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>Avoir des patients (106_avoir_patient.sql) : solde, mouvements, remboursement par le tiroir.</summary>
public sealed class AvoirsSql(NpgsqlDataSource db) : IAvoirs
{
    public async Task<decimal> SoldeAsync(long patientId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT COALESCE((SELECT solde FROM v_avoir_patient WHERE patient_id = $1), 0)");
        cmd.Parameters.AddWithValue(patientId);
        return (decimal)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<Page<MouvementAvoir>> MouvementsAsync(long patientId, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.sens, a.origine, a.montant,
                   SUM(CASE WHEN a.sens = 'credit' THEN a.montant ELSE -a.montant END) OVER (ORDER BY a.cree_le, a.id),
                   a.vente_id, v.numero, a.motif, a.cree_le, u.nom_complet, mt.numero
            FROM avoir_mouvement a
            JOIN utilisateur u ON u.id = a.utilisateur_id
            LEFT JOIN vente v ON v.id = a.vente_id
            LEFT JOIN mouvement_tiroir mt ON mt.id = a.mouvement_tiroir_id
            WHERE a.patient_id = $1
            ORDER BY a.cree_le DESC, a.id DESC LIMIT $2 OFFSET $3
            """);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<MouvementAvoir>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                lignes.Add(new MouvementAvoir(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetDecimal(3), r.GetDecimal(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.GetDateTime(8), r.GetString(9), r.IsDBNull(10) ? null : r.GetString(10)));
        await using var compte = db.CreateCommand("SELECT count(*)::int FROM avoir_mouvement WHERE patient_id = $1");
        compte.Parameters.AddWithValue(patientId);
        return new(lignes, (int)(await compte.ExecuteScalarAsync(ct))!, page.Page, page.Taille);
    }

    public async Task<MouvementTiroirEcrit> RembourserAsync(RemboursementAvoirAEcrire a, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Solde relu sous verrou du patient avant de sortir l'argent
        var solde = await EcritureAvoir.VerrouillerSoldeAsync(cx, tx, a.PatientId, ct);
        if (solde < a.Montant) throw RegleAvoir.Insuffisant(solde);
        await TiroirSql.ExigerDansTiroirAsync(cx, tx, a.SessionCaisseId, a.Montant, ct);
        string libelle;
        await using (var cmd = new NpgsqlCommand("SELECT libelle FROM motif_reglable WHERE liste = 'tiroir' AND code = 'remboursement_client'", cx, tx))
            libelle = await cmd.ExecuteScalarAsync(ct) as string ?? "Remboursement d’un client";
        long evenement;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO evenement_caisse (type, utilisateur_id, poste, session_caisse_id, montant, detail)
            VALUES ('tiroir_ouvert', $1, $2, $3, $4, $5) RETURNING id
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(a.UtilisateurId);
            cmd.Parameters.AddWithValue(a.Poste);
            cmd.Parameters.AddWithValue(a.SessionCaisseId);
            cmd.Parameters.AddWithValue(a.Montant);
            cmd.Parameters.AddWithValue(libelle);
            evenement = (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        var bon = await TiroirSql.InsererAsync(cx, tx, evenement, a.SessionCaisseId, a.UtilisateurId, a.Poste, "remboursement_client", libelle,
            RegleTiroir.Sortie, "patient", a.Montant, null, null, null, null, null, a.PatientId, $"Remboursement de l’avoir : {a.Motif}",
            null, null, a.ValidePar, null, null, ct);
        await EcritureAvoir.DebiterAsync(cx, tx, a.PatientId, a.Montant, RegleAvoir.OrigineRemboursement, null, null, bon.Id, a.Motif, a.UtilisateurId, ct);
        await tx.CommitAsync(ct);
        return bon;
    }
}
