using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
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
            -- Sans recherche : patients qui doivent de l'argent ; avec une recherche : aussi ceux dont la dette est soldée (relevé consultable)
            {Ou(2)}
            ORDER BY p.solde_credit DESC LIMIT 50
            """);
        Recherche(cmd, recherche);
        return await Lire(cmd, ct);
    }

    /// <summary>
    /// Critères de la recherche ($1 : texte, $<paramref name="numeroPrefixe"/> : début de numéro normalisé). Téléphone : par début de
    /// numéro, à partir de 4 chiffres, séparateurs ignorés, +213 = 0 (même règle qu'à la caisse, RechercheTelephone).
    /// </summary>
    private static string Ou(int numeroPrefixe) => $"""
        WHERE (p.solde_credit > 0 OR ($1::text IS NOT NULL AND EXISTS (SELECT 1 FROM credit_mouvement m WHERE m.patient_id = p.id)))
          AND ($1::text IS NULL OR concat_ws(' ', p.nom, p.prenom) ILIKE '%' || $1 || '%'
               OR (${numeroPrefixe}::text IS NOT NULL AND {Patients.TelephoneSql.Normalise("p.telephone")} LIKE ${numeroPrefixe} || '%'))
        """;

    private static void Recherche(NpgsqlCommand cmd, string? recherche)
    {
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)Pharma.Domain.Ventes.RechercheTelephone.Prefixe(recherche) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
    }

    public async Task<Page<PatientEnCredit>> AvecCreditPageAsync(string? recherche, DemandePage page, CancellationToken ct = default)
    {
        // Mêmes critères que AvecCreditAsync, sans plafond de 50 : page et total
        await using var cmd = db.CreateCommand($"{Select} {Ou(4)} ORDER BY p.solde_credit DESC, p.id LIMIT $2 OFFSET $3");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)recherche ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)Pharma.Domain.Ventes.RechercheTelephone.Prefixe(recherche) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        var lignes = await Lire(cmd, ct);
        await using var compte = db.CreateCommand($"SELECT count(*) FROM patient p {Ou(2)}");
        Recherche(compte, recherche);
        var total = Convert.ToInt32(await compte.ExecuteScalarAsync(ct));
        return new(lignes, total, page.Page, page.Taille);
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

        // Ventes à crédit avec ce qui est réglé et ce qui reste dû (038 : les plus anciennes soldées d'abord)
        var ventes = new List<VenteACredit>();
        await using (var cmd = db.CreateCommand("""
            SELECT numero, date_vente, montant, regle, reste FROM v_vente_credit
            WHERE patient_id = $1 ORDER BY (reste > 0) DESC, date_vente DESC LIMIT 50
            """))
        {
            cmd.Parameters.AddWithValue(patientId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                ventes.Add(new VenteACredit(r.GetString(0), r.GetDateTime(1), r.GetDecimal(2), r.GetDecimal(3), r.GetDecimal(4)));
        }

        // Relevé de la dette : première page des mouvements, du plus récent au plus ancien
        var mouvements = await MouvementsAsync(patientId, new DemandePage(), null, ct);

        decimal reprise;
        await using (var cmd = db.CreateCommand("SELECT COALESCE((SELECT montant FROM credit_ouverture WHERE patient_id = $1), 0)"))
        {
            cmd.Parameters.AddWithValue(patientId);
            reprise = (decimal)(await cmd.ExecuteScalarAsync(ct))!;
        }
        return new SituationCredit(patient, ventes, mouvements.Lignes, reprise, mouvements.Total);
    }

    public async Task<Page<MouvementCredit>> MouvementsAsync(long patientId, DemandePage page, FiltreReleve? filtre = null, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT m.cree_le, m.type, m.montant, m.solde_apres, v.numero, u.nom_complet, m.poste, m.reference, {Lecture.PaginationSql.Total}, m.utilisateur_id
            FROM credit_mouvement m
            LEFT JOIN vente v ON v.id = m.vente_id
            LEFT JOIN utilisateur u ON u.id = m.utilisateur_id
            WHERE m.patient_id = $1
              AND ($2::date IS NULL OR m.cree_le >= $2::date)
              AND ($3::date IS NULL OR m.cree_le < $3::date + 1)
              AND ($4::text IS NULL OR m.type = $4)
            ORDER BY m.id DESC
            {Lecture.PaginationSql.Limite(5)}
            """);
        cmd.Parameters.AddWithValue(patientId);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Du ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Au ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)filtre?.Type ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        Lecture.PaginationSql.Parametres(cmd, page);
        static string? S(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
        return await Lecture.PaginationSql.LireAsync(cmd, page, 8, r => new MouvementCredit(
            r.GetDateTime(0), r.GetString(1), r.GetDecimal(2), r.GetDecimal(3), S(r, 4), S(r, 5), S(r, 6), S(r, 7), r.IsDBNull(9) ? null : r.GetInt64(9)), ct);
    }

    public async Task<string> EncaisserAsync(long patientId, decimal montant, string mode, long sessionCaisseId, long utilisateurId, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Remboursement, null, utilisateurId, null, $"Règlement du patient ({mode})", ct, sessionCaisseId);
        // Dette diminuée seulement si elle couvre encore le montant (deux postes ne peuvent pas encaisser deux fois)
        await using (var cmd = new NpgsqlCommand(
            "UPDATE patient SET solde_credit = solde_credit - $2 WHERE id = $1 AND solde_credit >= $2", cx, tx))
        {
            cmd.Parameters.AddWithValue(patientId);
            cmd.Parameters.AddWithValue(montant);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La dette du patient vient de changer : rechargez sa situation.");
        }
        string id;
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO paiement (patient_id, mode, montant, utilisateur_id, session_caisse_id, numero_recu)
            VALUES ($1, $2, $3, $4, $5, 'RC-' || lpad(nextval('seq_numero_recu')::text, 6, '0')) RETURNING numero_recu
            """, cx, tx))
        {
            cmd.Parameters.AddWithValue(patientId);
            cmd.Parameters.AddWithValue(mode);
            cmd.Parameters.AddWithValue(montant);
            cmd.Parameters.AddWithValue(utilisateurId);
            cmd.Parameters.AddWithValue(sessionCaisseId);
            id = (string)(await cmd.ExecuteScalarAsync(ct))!;
        }
        await tx.CommitAsync(ct);
        return id;
    }

    public async Task CorrigerAsync(long patientId, decimal detteAttendue, decimal nouvelleDette, string motif, long utilisateurId, string? poste,
        CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        await ReleveCredit.ContexteAsync(cx, tx, ReleveCredit.Correction, null, utilisateurId, poste, motif, ct);
        // Seulement si la dette est encore celle que le titulaire a vue (un règlement a pu arriver entre-temps)
        await using (var cmd = new NpgsqlCommand("UPDATE patient SET solde_credit = $3 WHERE id = $1 AND solde_credit = $2", cx, tx))
        {
            cmd.Parameters.AddWithValue(patientId);
            cmd.Parameters.AddWithValue(detteAttendue);
            cmd.Parameters.AddWithValue(nouvelleDette);
            if (await cmd.ExecuteNonQueryAsync(ct) == 0) throw new RegleMetierException("La dette du patient vient de changer : rechargez sa situation.");
        }
        await tx.CommitAsync(ct);
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
