using Npgsql;
using NpgsqlTypes;

namespace Pharma.Cloud.Api.Reception;

public sealed record EtatCertificat(Guid Officine, string Serveur, DateTimeOffset Echeance, bool Revoque, bool Resiliee);

public sealed record CodeValable(Guid Officine, string Serveur, bool DejaActivee);

/// <summary>
/// Accès à la base centrale par le rôle applicatif (pharma_cloud_api, non propriétaire). Toute lecture ou écriture de données
/// reçues se fait dans une transaction où l'officine courante est posée (set_config('app.officine_id', …, true) = SET LOCAL) :
/// la Row Level Security cloisonne même une requête sans filtre.
/// </summary>
public sealed class DepotCentral(NpgsqlDataSource db)
{
    public async Task<CodeValable?> CodeValableAsync(string empreinteCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT officine_id, serveur, deja_activee FROM cloud_code_valable($1)");
        cmd.Parameters.AddWithValue(empreinteCode);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CodeValable(r.GetGuid(0), r.GetString(1), r.GetBoolean(2)) : null;
    }

    public async Task<bool> ActiverAsync(string empreinteCode, Guid officine, string empreinteCertificat, string serie, DateTimeOffset echeance, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_activer($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(empreinteCode);
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(empreinteCertificat);
        cmd.Parameters.AddWithValue(serie);
        cmd.Parameters.AddWithValue(echeance.UtcDateTime);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> RenouvelerAsync(string ancien, string nouveau, string serie, DateTimeOffset echeance, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_renouveler($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(ancien);
        cmd.Parameters.AddWithValue(nouveau);
        cmd.Parameters.AddWithValue(serie);
        cmd.Parameters.AddWithValue(echeance.UtcDateTime);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<EtatCertificat?> CertificatAsync(string empreinte, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT officine_id, serveur, echeance, revoque, resiliee FROM cloud_certificat($1)");
        cmd.Parameters.AddWithValue(empreinte);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new EtatCertificat(r.GetGuid(0), r.GetString(1), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)), r.GetBoolean(3), r.GetBoolean(4))
            : null;
    }

    public async Task JournaliserAsync(Guid? officine, string? certificat, string evenement, string? detail, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_journaliser($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)certificat ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(evenement);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)detail ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Dépose un lot contrôlé pour l'officine du certificat : (statut, dernier lot reçu).</summary>
    public async Task<(string Statut, long Dernier)> DeposerAsync(Guid officine, int epoque, long numero, string empreinte, string version,
        byte[] contenu, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT statut, dernier FROM cloud_appliquer_lot($1, $2, $3, $4, $5)", cnx, tx);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(empreinte);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(contenu);
        (string, long) res;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            res = (r.GetString(0), r.GetInt64(1));
        }
        await tx.CommitAsync(ct);
        return res;
    }

    public async Task<long> DernierLotAsync(Guid officine, int epoque, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT cloud_dernier_lot($1)", cnx, tx);
        cmd.Parameters.AddWithValue(epoque);
        var n = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return n;
    }

    private static async Task PoserOfficineAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, Guid officine, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx);
        cmd.Parameters.AddWithValue(officine.ToString("D"));
        await cmd.ExecuteScalarAsync(ct);
    }
}
