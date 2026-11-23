using Npgsql;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Relevés en lecture seule pour la détection d'un serveur écarté (bascule du 2e serveur, lot 1) : base locale et base de
/// SecondServeur:AutreServeur (même chaîne de connexion, hôte remplacé, délai 3 s, sans pool : on veut l'état du moment).
/// Aucune écriture, aucun secret dans les exceptions remontées : toute indisponibilité donne « inconnu » (null).
/// </summary>
public sealed class ReleveServeursSql(string chaineConnexion, string? autreServeur) : IReleveServeurs
{
    private const int DelaiSecondes = 3;

    public bool AutreServeurRegle => !string.IsNullOrWhiteSpace(autreServeur) && !string.IsNullOrWhiteSpace(chaineConnexion);
    public string? AutreServeur => string.IsNullOrWhiteSpace(autreServeur) ? null : autreServeur.Trim();

    public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) => LireHoteAsync(null, ct);

    public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) => AutreServeurRegle ? LireHoteAsync(AutreServeur, ct) : Task.FromResult<ReleveServeur?>(null);

    private async Task<ReleveServeur?> LireHoteAsync(string? hote, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(chaineConnexion)) return null;
        try
        {
            var b = new NpgsqlConnectionStringBuilder(chaineConnexion) { Timeout = DelaiSecondes, Pooling = false };
            if (hote is not null) b.Host = hote;
            await using var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            return await LireAsync(c, ct);
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return null;
        }
    }

    /// <summary>Lecture sur une connexion ouverte : recovery, puis (si en écriture) timeline et identifiant système.</summary>
    public static async Task<ReleveServeur?> LireAsync(NpgsqlConnection c, CancellationToken ct = default)
    {
        bool enRecovery;
        await using (var cmd = new NpgsqlCommand("SELECT pg_is_in_recovery()", c))
            enRecovery = (bool)(await cmd.ExecuteScalarAsync(ct))!;
        if (enRecovery) return new ReleveServeur(true, null, null);

        // Timeline courante : 8 premiers caractères hexadécimaux du nom du fichier WAL courant (même lecture qu'au démarrage ;
        // pas de droit particulier). Le plan prévoyait pg_control_checkpoint en premier : il peut retarder sur une promotion récente.
        long? timeline = null;
        await using (var cmd = new NpgsqlCommand("SELECT substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8)", c))
            if (await cmd.ExecuteScalarAsync(ct) is string hex) timeline = Convert.ToInt64(hex, 16);

        long? identifiant = null;
        try
        {
            await using var cmd = new NpgsqlCommand("SELECT system_identifier FROM pg_control_system()", c);
            if (await cmd.ExecuteScalarAsync(ct) is long id) identifiant = id;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }   // droits insuffisants : lignée inconnue
        return new ReleveServeur(false, timeline, identifiant);
    }
}
