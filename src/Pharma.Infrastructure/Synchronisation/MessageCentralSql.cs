using Npgsql;
using Pharma.Application.Synchronisation;

namespace Pharma.Infrastructure.Synchronisation;

/// <summary>
/// Reste du lot S7.3 (script 160) : messages du titulaire à distance appliqués et pas encore fermés (index partiel
/// ix_sync_ordre_recu_message), fermeture par sync_message_central_fermer (journal des actions dans la même transaction).
/// </summary>
public sealed class MessageCentralSql(NpgsqlDataSource db) : IMessageCentralDepot
{
    public async Task<IReadOnlyList<MessageCentralLu>> AffichablesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, donnees::text, cree_le, recu_le FROM (
                SELECT id, donnees, cree_le, recu_le, message_ferme_le FROM sync_ordre_recu
                WHERE type = 'message' AND statut = 'applique'
                ORDER BY cree_le DESC, id DESC LIMIT 1) dernier
            WHERE message_ferme_le IS NULL
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<MessageCentralLu>();
        while (await r.ReadAsync(ct))
            liste.Add(new MessageCentralLu(r.GetGuid(0), r.GetString(1), Utc(r.GetDateTime(2)), Utc(r.GetDateTime(3))));
        return liste;
    }

    public async Task<bool> FermerAsync(Guid id, long par, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT sync_message_central_fermer($1, $2)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(par);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    private static DateTime Utc(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc);
}
