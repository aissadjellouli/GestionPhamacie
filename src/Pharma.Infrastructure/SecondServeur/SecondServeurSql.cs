using Npgsql;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>Tables du script 122 (serveur_officine, surveillance_replication) et vues de réplication de PostgreSQL.</summary>
public sealed class SecondServeurSql(NpgsqlDataSource db) : ISecondServeurDepot
{
    private const string Colonnes = """
        nom, role, dossier_chifa, dossier_mdn, chifa_installe, lecteur_token, mdn_installe, douchette_testee, version_api, cle_presente,
        empreinte_cle, imprimantes, dossier_chifa_joignable, dossier_mdn_joignable, releve_le
        """;

    public async Task<IReadOnlyList<ServeurOfficine>> ServeursAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM serveur_officine ORDER BY role <> 'principal', nom");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ServeurOfficine>();
        while (await r.ReadAsync(ct))
            liste.Add(new ServeurOfficine(r.GetString(0), r.GetString(1), Texte(r, 2), Texte(r, 3), r.GetBoolean(4), r.GetBoolean(5), r.GetBoolean(6),
                r.GetBoolean(7), Texte(r, 8), Booleen(r, 9), Texte(r, 10), Texte(r, 11), Booleen(r, 12), Booleen(r, 13),
                r.IsDBNull(14) ? null : r.GetDateTime(14)));
        return liste;
    }

    public async Task<EtatReplication> ReplicationAsync(string slot, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenConnectionAsync(ct);
        bool enRecovery, slotExiste, slotActif;
        await using (var cmd = new NpgsqlCommand("""
            SELECT pg_is_in_recovery(),
                   EXISTS (SELECT 1 FROM pg_replication_slots WHERE slot_name = $1),
                   COALESCE((SELECT bool_or(active) FROM pg_replication_slots WHERE slot_name = $1), false)
            """, connexion))
        {
            cmd.Parameters.AddWithValue(slot);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            (enRecovery, slotExiste, slotActif) = (r.GetBoolean(0), r.GetBoolean(1), r.GetBoolean(2));
        }

        if (enRecovery)
        {
            // Vu du 2e serveur : réception des WAL et retard de rejeu
            await using var cmd = new NpgsqlCommand("""
                SELECT (SELECT status FROM pg_stat_wal_receiver LIMIT 1),
                       (SELECT sender_host FROM pg_stat_wal_receiver LIMIT 1),
                       pg_wal_lsn_diff(pg_last_wal_receive_lsn(), pg_last_wal_replay_lsn())::bigint,
                       EXTRACT(EPOCH FROM now() - pg_last_xact_replay_timestamp())::float8
                """, connexion);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            var etat = Texte(r, 0);
            return new EtatReplication(true, etat == "streaming", etat, Texte(r, 1), r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetDouble(3), slotExiste, slotActif, etat is not null);
        }

        await using (var cmd = new NpgsqlCommand("""
            SELECT state, COALESCE(application_name, '') || COALESCE(' ' || host(client_addr), ''),
                   pg_wal_lsn_diff(pg_current_wal_lsn(), replay_lsn)::bigint, EXTRACT(EPOCH FROM replay_lag)::float8
            FROM pg_stat_replication
            ORDER BY (state = 'streaming') DESC NULLS LAST
            LIMIT 1
            """, connexion))
        {
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (!await r.ReadAsync(ct))
                return new EtatReplication(false, false, null, null, null, null, slotExiste, slotActif, true);
            var etat = Texte(r, 0);
            // Sans pg_monitor, PostgreSQL cache l'état et les positions : la ligne seule prouve la connexion
            var visibles = etat is not null;
            return new EtatReplication(false, etat is null or "streaming" or "catchup", etat, Texte(r, 1)?.Trim() is { Length: > 0 } c ? c : null,
                r.IsDBNull(2) ? null : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetDouble(3), slotExiste, slotActif, visibles);
        }
    }

    public async Task<ReglagesReplication> ReglagesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT seuil_secondes, seuil_octets, derniere_alerte_le, ged_copie_minutes FROM surveillance_replication WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ReglagesReplication(r.GetInt32(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetDateTime(2), r.GetInt32(3)) : new ReglagesReplication();
    }

    public async Task EnregistrerSeuilsAsync(int seuilSecondes, long seuilOctets, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO surveillance_replication (id, seuil_secondes, seuil_octets, modifie_le) VALUES (1, $1, $2, now())
            ON CONFLICT (id) DO UPDATE SET seuil_secondes = EXCLUDED.seuil_secondes, seuil_octets = EXCLUDED.seuil_octets, modifie_le = now()
            """);
        cmd.Parameters.AddWithValue(seuilSecondes);
        cmd.Parameters.AddWithValue(seuilOctets);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnregistrerCopieGedAsync(int minutes, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO surveillance_replication (id, ged_copie_minutes, modifie_le) VALUES (1, $1, now())
            ON CONFLICT (id) DO UPDATE SET ged_copie_minutes = EXCLUDED.ged_copie_minutes, modifie_le = now()
            """);
        cmd.Parameters.AddWithValue(minutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterAlerteAsync(DateTime? alerteUtc, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE surveillance_replication SET derniere_alerte_le = $1 WHERE id = 1");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)alerteUtc ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.TimestampTz });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<bool> ModifierServeurAsync(string nom, ModificationServeur m, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE serveur_officine
            SET dossier_chifa = $2, dossier_mdn = $3, chifa_installe = $4, lecteur_token = $5, mdn_installe = $6, douchette_testee = $7,
                modifie_le = now(), modifie_par = $8
            WHERE nom = $1
            """);
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)m.DossierChifa ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)m.DossierMdn ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(m.ChifaInstalle);
        cmd.Parameters.AddWithValue(m.LecteurToken);
        cmd.Parameters.AddWithValue(m.MdnInstalle);
        cmd.Parameters.AddWithValue(m.DouchetteTestee);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task DeclarerPrincipalAsync(ServeurOfficine releve, CancellationToken ct = default)
    {
        await using var connexion = await db.OpenConnectionAsync(ct);
        await using var tx = await connexion.BeginTransactionAsync(ct);
        await using (var cmd = new NpgsqlCommand("UPDATE serveur_officine SET role = 'second', modifie_le = now() WHERE role = 'principal' AND nom <> $1", connexion, tx))
        {
            cmd.Parameters.AddWithValue(releve.Nom);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("""
            INSERT INTO serveur_officine (nom, role, version_api, cle_presente, empreinte_cle, dossier_chifa_joignable, dossier_mdn_joignable, releve_le)
            VALUES ($1, 'principal', $2, $3, $4, $5, $6, now())
            ON CONFLICT (nom) DO UPDATE SET role = 'principal', version_api = EXCLUDED.version_api, cle_presente = EXCLUDED.cle_presente,
                empreinte_cle = EXCLUDED.empreinte_cle, dossier_chifa_joignable = EXCLUDED.dossier_chifa_joignable,
                dossier_mdn_joignable = EXCLUDED.dossier_mdn_joignable, releve_le = now(), modifie_le = now()
            """, connexion, tx))
        {
            cmd.Parameters.AddWithValue(releve.Nom);
            cmd.Parameters.Add(Valeur(releve.VersionApi, NpgsqlTypes.NpgsqlDbType.Text));
            cmd.Parameters.Add(Valeur(releve.ClePresente, NpgsqlTypes.NpgsqlDbType.Boolean));
            cmd.Parameters.Add(Valeur(releve.EmpreinteCle, NpgsqlTypes.NpgsqlDbType.Text));
            cmd.Parameters.Add(Valeur(releve.DossierChifaJoignable, NpgsqlTypes.NpgsqlDbType.Boolean));
            cmd.Parameters.Add(Valeur(releve.DossierMdnJoignable, NpgsqlTypes.NpgsqlDbType.Boolean));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    private static NpgsqlParameter Valeur(object? v, NpgsqlTypes.NpgsqlDbType type) => new() { Value = v ?? DBNull.Value, NpgsqlDbType = type };
    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static bool? Booleen(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
}

/// <summary>Ce que l'API lit au démarrage, avant de servir : rôle de sa base, dossiers propres à ce serveur, autre serveur.</summary>
/// <summary>Timeline : n° de la ligne temporelle de la base principale (monte à chaque promotion) ; null en lecture seule ou inconnu.</summary>
public sealed record LectureDemarrage(bool? BaseEnRecovery, string? DossierChifa, string? DossierMdn, long? Timeline, bool? AutreServeurPrincipal, long? TimelineAutre);

public static class DemarrageServeur
{
    /// <summary>Nom de ce serveur dans serveur_officine (nom du PC, en majuscules).</summary>
    public static string NomCeServeur => Environment.MachineName.Trim().ToUpperInvariant();

    /// <summary>Lecture tolérante : base injoignable ou script 122 absent, valeurs inconnues (null) et rien n'est refusé ici.</summary>
    public static async Task<LectureDemarrage> LireAsync(string chaineConnexion, string? autreServeur, CancellationToken ct = default)
    {
        bool? enRecovery = null;
        long? timeline = null;
        string? chifa = null, mdn = null;
        if (!string.IsNullOrWhiteSpace(chaineConnexion))
        {
            try
            {
                var b = new NpgsqlConnectionStringBuilder(chaineConnexion) { Timeout = 5 };
                await using var c = new NpgsqlConnection(b.ConnectionString);
                await c.OpenAsync(ct);
                await using (var cmd = new NpgsqlCommand("SELECT pg_is_in_recovery()", c))
                    enRecovery = (bool)(await cmd.ExecuteScalarAsync(ct))!;
                if (enRecovery == false) timeline = await TimelineAsync(c, ct);
                await using (var cmd = new NpgsqlCommand("""
                    SELECT s.dossier_chifa, s.dossier_mdn FROM serveur_officine s WHERE s.nom = $1
                    """, c))
                {
                    cmd.Parameters.AddWithValue(NomCeServeur);
                    try
                    {
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        if (await r.ReadAsync(ct)) (chifa, mdn) = (r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
                    }
                    catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UndefinedTable) { }
                }
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException) { }
        }
        var (autre, timelineAutre) = await AutrePrincipalAsync(chaineConnexion, autreServeur, ct);
        return new LectureDemarrage(enRecovery, chifa, mdn, timeline, autre, timelineAutre);
    }

    /// <summary>Vrai si l'autre serveur répond et que sa base n'est pas en lecture seule (deux principaux) ; null s'il ne répond pas.</summary>
    private static async Task<(bool?, long?)> AutrePrincipalAsync(string chaineConnexion, string? autreServeur, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(autreServeur) || string.IsNullOrWhiteSpace(chaineConnexion)) return (null, null);
        try
        {
            var b = new NpgsqlConnectionStringBuilder(chaineConnexion) { Host = autreServeur.Trim(), Timeout = 3 };
            await using var c = new NpgsqlConnection(b.ConnectionString);
            await c.OpenAsync(ct);
            await using (var cmd = new NpgsqlCommand("SELECT pg_is_in_recovery()", c))
                if ((bool)(await cmd.ExecuteScalarAsync(ct))!) return (false, null);
            return (true, await TimelineAsync(c, ct));
        }
        catch (Exception e) when (e is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException or System.Net.Sockets.SocketException)
        {
            return (null, null);
        }
    }

    // Timeline de la base principale, lue dans le nom du fichier WAL courant (8 premiers caractères hexadécimaux)
    private static async Task<long?> TimelineAsync(NpgsqlConnection c, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT substr(pg_walfile_name(pg_current_wal_lsn()), 1, 8)", c);
        return await cmd.ExecuteScalarAsync(ct) is string hex ? Convert.ToInt64(hex, 16) : null;
    }
}
