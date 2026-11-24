using Npgsql;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Verrou de la base d'un serveur écarté (bascule du 2e serveur, lot 3) : chaque connexion PHYSIQUE ouverte par le pool applique
/// « SET default_transaction_read_only = on » ; toute écriture est alors refusée par PostgreSQL (SQLSTATE 25006). Le pool est vidé
/// au passage (les connexions déjà ouvertes sont fermées à leur retour). Le pool est créé avec « No Reset On Close » : sans lui,
/// le DISCARD ALL de la remise au pool effacerait le réglage dès la première réutilisation.
/// </summary>
public sealed class ConnexionsLectureSeule : IVerrouBase
{
    /// <summary>Nom d'application des connexions de l'API (pg_stat_activity.application_name) : le retrait (cas 2) attend la fin de leurs transactions.</summary>
    public const string NomApplicationApi = "PharmaOfficine.Api";

    private volatile bool _actif;

    public bool Actif => _actif;

    /// <summary>Source de connexions à vider au changement d'état (posée à la création de la source).</summary>
    public NpgsqlDataSource? Source { get; set; }

    public void Appliquer(bool lectureSeule)
    {
        _actif = lectureSeule;   // d'abord l'état : toute connexion ouverte à partir d'ici le lit
        Source?.Clear();         // puis les connexions déjà ouvertes (libres : fermées ; occupées : fermées à leur retour)
    }

    public void Initialiser(NpgsqlConnection cnx)
    {
        if (!_actif) return;
        using var cmd = new NpgsqlCommand("SET default_transaction_read_only = on", cnx);
        cmd.ExecuteNonQuery();
    }

    public async Task InitialiserAsync(NpgsqlConnection cnx)
    {
        if (!_actif) return;
        await using var cmd = new NpgsqlCommand("SET default_transaction_read_only = on", cnx);
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Source de connexions de l'API : fuseau de l'officine, verrou de lecture seule et pas de remise à zéro à la fermeture.</summary>
    public NpgsqlDataSource CreerSource(string chaineConnexion)
    {
        var builder = new NpgsqlDataSourceBuilder(chaineConnexion);
        builder.ConnectionStringBuilder.NoResetOnClose = true;
        if (string.IsNullOrWhiteSpace(builder.ConnectionStringBuilder.ApplicationName)) builder.ConnectionStringBuilder.ApplicationName = NomApplicationApi;
        builder.UsePhysicalConnectionInitializer(Initialiser, InitialiserAsync);
        var source = builder.Build();
        Source = source;
        return source;
    }
}

/// <summary>
/// Écart gardé dans serveur_ecarte (script 163). Source de connexions PROPRE (sans le verrou de lecture seule) : l'écart doit pouvoir
/// s'écrire et se lever alors que les connexions de l'API sont déjà verrouillées ; ce sont les seules écritures permises à un serveur écarté.
/// </summary>
public sealed class EcartsSql(NpgsqlDataSource sourceEcriture) : IEcartsDepot
{
    public const string NatureBascule = "bascule", NatureRetrait = "retrait";

    public async Task<EcartDetecte?> LireActifAsync(CancellationToken ct = default)
    {
        await using var cmd = sourceEcriture.CreateCommand("""
            SELECT ecarte_le, serveur_retenu, timeline_locale, timeline_retenue, nature FROM serveur_ecarte WHERE leve_le IS NULL
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        // Retrait (script 166) : timeline du serveur retenu inconnue (NULL), lue 0 ; l'écart est gardé en base et collant
        return await r.ReadAsync(ct)
            ? new EcartDetecte(DateTime.SpecifyKind(r.GetDateTime(0), DateTimeKind.Utc), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? 0 : r.GetInt32(3),
                Retrait: r.GetString(4) == NatureRetrait)
            : null;
    }

    public async Task<bool> EnregistrerAsync(EcartDetecte ecart, string motif, CancellationToken ct = default)
    {
        await using var cx = await sourceEcriture.OpenConnectionAsync(ct);
        var identifiant = "inconnu";   // lignée illisible sans le droit sur pg_control_system (script 163b du lot 6) : la colonne est obligatoire
        try
        {
            await using var lire = new NpgsqlCommand("SELECT system_identifier::text FROM pg_control_system()", cx);
            if (await lire.ExecuteScalarAsync(ct) is string id) identifiant = id;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.InsufficientPrivilege) { }

        await using var cmd = new NpgsqlCommand("""
            INSERT INTO serveur_ecarte (ecarte_le, serveur_retenu, timeline_locale, timeline_retenue, identifiant_systeme, motif, nature)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            ON CONFLICT DO NOTHING
            """, cx);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(ecart.DepuisUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(ecart.ServeurRetenu ?? "inconnu");
        cmd.Parameters.AddWithValue((int)ecart.TimelineLocale);
        cmd.Parameters.Add(new NpgsqlParameter { Value = ecart.Retrait ? DBNull.Value : (int)ecart.TimelineRetenue, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Integer });
        cmd.Parameters.AddWithValue(identifiant);
        cmd.Parameters.AddWithValue(motif.Length > 500 ? motif[..500] : motif);
        cmd.Parameters.AddWithValue(ecart.Retrait ? NatureRetrait : NatureBascule);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }

    public async Task<bool> LeverAsync(string par, string motif, CancellationToken ct = default)
    {
        await using var cmd = sourceEcriture.CreateCommand("""
            UPDATE serveur_ecarte SET leve_le = now(), leve_par = $1, leve_motif = $2 WHERE leve_le IS NULL
            """);
        cmd.Parameters.AddWithValue(par.Length > 200 ? par[..200] : par);
        cmd.Parameters.AddWithValue(motif.Length > 500 ? motif[..500] : motif);
        return await cmd.ExecuteNonQueryAsync(ct) > 0;
    }
}
