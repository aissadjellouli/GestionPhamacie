using Npgsql;
using Pharma.Application.SecondServeur;

namespace Pharma.Infrastructure.SecondServeur;

/// <summary>
/// Présence des postes (script 165, table presence_poste). Écriture : UPSERT de l'instant « vu » (jamais appelée en veille ni en lecture seule).
/// Lecture : les postes et l'instant de référence de la copie. Référence = pg_last_xact_replay_timestamp() sur le 2e serveur (copie en recovery : dernière
/// transaction rejouée, JAMAIS now() ; null si rien n'a été rejoué), now() sur le serveur 1 (voir <see cref="PostesConnus.Calculer"/> : après une coupure la
/// copie est figée, avec l'heure courante la liste fondrait à zéro et un seul vote suffirait).
/// </summary>
public sealed class PresencePostesSql(NpgsqlDataSource db) : IPostesConnusDepot, IPresenceDepot
{
    public async Task NoterAsync(string poste, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO presence_poste (poste, vu_le) VALUES (@poste, now()) ON CONFLICT (poste) DO UPDATE SET vu_le = now()");
        cmd.Parameters.AddWithValue("poste", poste);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PostesConnusLus> LireAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH ref AS (SELECT CASE WHEN pg_is_in_recovery() THEN pg_last_xact_replay_timestamp() ELSE now() END AS t)
            SELECT ref.t, p.poste, p.vu_le
            FROM ref LEFT JOIN presence_poste p ON p.vu_le > ref.t - interval '1 day'
            """);
        DateTime? reference = null;
        var postes = new List<PosteVuEnBase>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            if (!r.IsDBNull(0)) reference = r.GetDateTime(0).ToUniversalTime();
            if (!r.IsDBNull(1)) postes.Add(new PosteVuEnBase(r.GetString(1), r.GetDateTime(2).ToUniversalTime()));
        }
        return new PostesConnusLus(reference, postes);
    }
}

/// <summary>La copie répliquée est-elle connectée à ce serveur ? Vrai si pg_stat_replication a au moins une ligne. Illisible : null (aucune décision).</summary>
public sealed class CopieConnecteeSql(NpgsqlDataSource db) : ICopieConnectee
{
    public async Task<bool?> ConnecteeAsync(CancellationToken ct = default)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_replication)");
            return await cmd.ExecuteScalarAsync(ct) is true;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }
}
