using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Notifications;
using Pharma.Domain.Notifications;

namespace Pharma.Infrastructure.Notifications;

/// <summary>Réglages, destinataires et file d'attente des messages (PostgreSQL).</summary>
public sealed class NotificationsSql(NpgsqlDataSource db) : INotificationsDepot
{
    // ---- Réglages -----------------------------------------------------------

    public async Task<ReglagesNotification> ReglagesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT smtp_serveur, smtp_port, smtp_tls, smtp_expediteur, smtp_identifiant, smtp_mot_de_passe IS NOT NULL,
                   modem_port, heure_rapport, dernier_rapport, sms_seuil_ecart, sms_lot_bloque, sms_sauvegarde
            FROM parametre_notification WHERE id = 1
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Réglages des notifications absents : exécutez database/013_notifications.sql.");
        return new ReglagesNotification(
            Texte(r, 0), r.GetInt32(1), r.GetBoolean(2), Texte(r, 3), Texte(r, 4), r.GetBoolean(5),
            Texte(r, 6), TimeOnly.FromTimeSpan(r.GetTimeSpan(7)), r.IsDBNull(8) ? null : r.GetFieldValue<DateOnly>(8),
            r.IsDBNull(9) ? null : r.GetDecimal(9), r.GetBoolean(10), r.GetBoolean(11));
    }

    public async Task EnregistrerReglagesAsync(ModificationReglagesNotification m, bool changerMotDePasse, byte[]? motDePasseChiffre, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE parametre_notification SET
                smtp_serveur = $1, smtp_port = $2, smtp_tls = $3, smtp_expediteur = $4, smtp_identifiant = $5,
                smtp_mot_de_passe = CASE WHEN $6 THEN $7 ELSE smtp_mot_de_passe END,
                modem_port = $8, heure_rapport = $9, sms_seuil_ecart = $10, sms_lot_bloque = $11, sms_sauvegarde = $12
            WHERE id = 1
            """);
        cmd.Parameters.Add(Nul(m.SmtpServeur, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(m.SmtpPort);
        cmd.Parameters.AddWithValue(m.SmtpTls);
        cmd.Parameters.Add(Nul(m.SmtpExpediteur, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(m.SmtpIdentifiant, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(changerMotDePasse);
        cmd.Parameters.Add(Nul(motDePasseChiffre, NpgsqlDbType.Bytea));
        cmd.Parameters.Add(Nul(m.ModemPort, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(m.HeureRapport.ToTimeSpan());
        cmd.Parameters.Add(Nul(m.SmsSeuilEcart, NpgsqlDbType.Numeric));
        cmd.Parameters.AddWithValue(m.SmsLotBloque);
        cmd.Parameters.AddWithValue(m.SmsSauvegarde);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<byte[]?> MotDePasseChiffreAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT smtp_mot_de_passe FROM parametre_notification WHERE id = 1");
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task NoterRapportEnvoyeAsync(DateOnly jour, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_notification SET dernier_rapport = $1 WHERE id = 1");
        cmd.Parameters.AddWithValue(jour);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Destinataires ------------------------------------------------------

    public async Task<IReadOnlyList<Destinataire>> DestinatairesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom, email, telephone, canal_email, canal_sms, types, valide, actif
            FROM destinataire_notification ORDER BY actif DESC, nom
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<Destinataire>();
        while (await r.ReadAsync(ct))
            liste.Add(new Destinataire(r.GetInt64(0), r.GetString(1), Texte(r, 2), Texte(r, 3), r.GetBoolean(4), r.GetBoolean(5),
                r.GetFieldValue<string[]>(6).Select(CodesNotification.TypeDepuisCode).ToHashSet(), r.GetBoolean(7), r.GetBoolean(8)));
        return liste;
    }

    public async Task<long> CreerDestinataireAsync(SaisieDestinataire d, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO destinataire_notification (nom, email, telephone, canal_email, canal_sms, types, actif)
            VALUES ($1, $2, $3, $4, $5, $6, $7) RETURNING id
            """);
        Parametres(cmd, d);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> ModifierDestinataireAsync(long id, SaisieDestinataire d, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE destinataire_notification
            SET nom = $1, email = $2, telephone = $3, canal_email = $4, canal_sms = $5, types = $6, actif = $7
            WHERE id = $8
            """);
        Parametres(cmd, d);
        cmd.Parameters.AddWithValue(id);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> MarquerValideAsync(long id, bool valide, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE destinataire_notification SET valide = $2 WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(valide);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    private static void Parametres(NpgsqlCommand cmd, SaisieDestinataire d)
    {
        cmd.Parameters.AddWithValue(d.Nom!);
        cmd.Parameters.Add(Nul(d.Email, NpgsqlDbType.Text));
        cmd.Parameters.Add(Nul(d.Telephone, NpgsqlDbType.Text));
        cmd.Parameters.AddWithValue(d.CanalEmail);
        cmd.Parameters.AddWithValue(d.CanalSms);
        cmd.Parameters.AddWithValue(d.Types.Select(t => t.Code()).Distinct().ToArray());
        cmd.Parameters.AddWithValue(d.Actif);
    }

    // ---- File d'attente -----------------------------------------------------

    public async Task AjouterMessagesAsync(IReadOnlyList<NouveauMessage> messages, CancellationToken ct = default)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        foreach (var m in messages)
        {
            await using var cmd = new NpgsqlCommand("""
                INSERT INTO message_sortant (canal, destinataire_id, adresse, type, objet, corps, corps_html, prochaine_tentative)
                VALUES ($1, $2, $3, $4, $5, $6, $7, now())
                """, cnx, tx);
            cmd.Parameters.AddWithValue(m.Canal.Code());
            cmd.Parameters.AddWithValue(m.DestinataireId);
            cmd.Parameters.AddWithValue(m.Adresse);
            cmd.Parameters.AddWithValue(m.Type.Code());
            cmd.Parameters.Add(Nul(m.Objet, NpgsqlDbType.Text));
            cmd.Parameters.AddWithValue(m.Corps);
            cmd.Parameters.Add(Nul(m.CorpsHtml, NpgsqlDbType.Text));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private const string SelectMessage = """
        SELECT m.id, m.canal, d.nom, m.adresse, m.type, m.objet, m.corps, m.corps_html, m.statut, m.tentatives,
               m.prochaine_tentative, m.derniere_erreur, m.cree_le, m.envoye_le
        FROM message_sortant m JOIN destinataire_notification d ON d.id = m.destinataire_id
        """;

    public async Task<IReadOnlyList<MessageEnFile>> MessagesAEnvoyerAsync(DateTime maintenantUtc, int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectMessage + """

            WHERE m.statut = 'en_attente' AND m.prochaine_tentative <= $1
            ORDER BY CASE m.canal WHEN 'sms' THEN 0 ELSE 1 END, m.prochaine_tentative
            LIMIT $2
            """);
        cmd.Parameters.AddWithValue(maintenantUtc);
        cmd.Parameters.AddWithValue(limite);
        return await LireMessages(cmd, ct);
    }

    public async Task<IReadOnlyList<MessageEnFile>> FileAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(SelectMessage + " ORDER BY m.cree_le DESC LIMIT $1");
        cmd.Parameters.AddWithValue(limite);
        return await LireMessages(cmd, ct);
    }

    public async Task MarquerEnvoyeAsync(long id, DateTime quandUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE message_sortant SET statut = 'envoye', envoye_le = $2, tentatives = tentatives + 1,
                   prochaine_tentative = NULL, derniere_erreur = NULL
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(quandUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task MarquerEchecAsync(long id, string erreur, DateTime? prochaineTentativeUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE message_sortant SET tentatives = tentatives + 1, derniere_erreur = left($2, 500),
                   prochaine_tentative = $3, statut = CASE WHEN $3 IS NULL THEN 'echec' ELSE 'en_attente' END
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(erreur);
        cmd.Parameters.Add(Nul(prochaineTentativeUtc, NpgsqlDbType.TimestampTz));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<IReadOnlyList<MessageEnFile>> LireMessages(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<MessageEnFile>();
        while (await r.ReadAsync(ct))
            liste.Add(new MessageEnFile(
                r.GetInt64(0), r.GetString(1) == "sms" ? CanalNotification.Sms : CanalNotification.Email, r.GetString(2), r.GetString(3),
                CodesNotification.TypeDepuisCode(r.GetString(4)), Texte(r, 5), r.GetString(6), Texte(r, 7), r.GetString(8), r.GetInt32(9),
                r.IsDBNull(10) ? null : r.GetDateTime(10), Texte(r, 11), r.GetDateTime(12), r.IsDBNull(13) ? null : r.GetDateTime(13)));
        return liste;
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    private static NpgsqlParameter Nul<T>(T? valeur, NpgsqlDbType type) =>
        new() { Value = (object?)valeur ?? DBNull.Value, NpgsqlDbType = type };
}
