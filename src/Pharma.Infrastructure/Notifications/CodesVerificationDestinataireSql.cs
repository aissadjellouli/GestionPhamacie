using Npgsql;
using Pharma.Application.Notifications;

namespace Pharma.Infrastructure.Notifications;

/// <summary>Codes de vérification des destinataires (068_verification_destinataire.sql).</summary>
public sealed class CodesVerificationDestinataireSql(NpgsqlDataSource db) : ICodesVerificationDestinataire
{
    public async Task EnregistrerAsync(long destinataireId, string hash, DateTime expireLeUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE destinataire_notification
            SET code_verification_hash = $2, code_verification_expire = $3, code_verification_essais = 0
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(destinataireId);
        cmd.Parameters.AddWithValue(hash);
        cmd.Parameters.AddWithValue(expireLeUtc);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<CodeVerification?> LireAsync(long destinataireId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT code_verification_hash, code_verification_expire, code_verification_essais
            FROM destinataire_notification WHERE id = $1 AND code_verification_hash IS NOT NULL
            """);
        cmd.Parameters.AddWithValue(destinataireId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CodeVerification(r.GetString(0), r.GetDateTime(1), r.GetInt32(2)) : null;
    }

    // Compté en base, ligne verrouillée : deux saisies simultanées ne dépassent pas le nombre d'essais permis
    public async Task<int> NoterEssaiAsync(long destinataireId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE destinataire_notification SET code_verification_essais = code_verification_essais + 1
            WHERE id = $1 RETURNING code_verification_essais
            """);
        cmd.Parameters.AddWithValue(destinataireId);
        return (int)(await cmd.ExecuteScalarAsync(ct) ?? 0);
    }

    public async Task EffacerAsync(long destinataireId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE destinataire_notification
            SET code_verification_hash = NULL, code_verification_expire = NULL, code_verification_essais = 0
            WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(destinataireId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
