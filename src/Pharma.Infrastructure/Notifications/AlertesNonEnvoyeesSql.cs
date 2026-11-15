using Npgsql;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Domain.Notifications;

namespace Pharma.Infrastructure.Notifications;

/// <summary>Alertes au titulaire qui n'ont pas pu partir (table alerte_non_envoyee, script 077) : bandeau de l'accueil du titulaire.</summary>
public sealed class AlertesNonEnvoyeesSql(NpgsqlDataSource db) : IAlertesNonEnvoyees
{
    public async Task NoterAsync(TypeNotification type, string objet, string raison, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("INSERT INTO alerte_non_envoyee (type, objet, raison) VALUES ($1, left($2, 300), left($3, 500))");
        cmd.Parameters.AddWithValue(type.Code());
        cmd.Parameters.AddWithValue(objet);
        cmd.Parameters.AddWithValue(raison);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<AlertesNonEnvoyeesNonVues> NonVuesAsync(string? texte, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, type, objet, raison, cree_le, count(*) OVER ()
            FROM alerte_non_envoyee WHERE vue_le IS NULL
              AND ($3::text IS NULL OR objet ILIKE '%' || $3 || '%' OR raison ILIKE '%' || $3 || '%')
            ORDER BY id DESC LIMIT $1 OFFSET $2
            """);
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)texte ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        var liste = new List<AlerteNonEnvoyee>();
        var nombre = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            liste.Add(new AlerteNonEnvoyee(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDateTime(4)));
            nombre = (int)r.GetInt64(5);
        }
        return new AlertesNonEnvoyeesNonVues(nombre, liste);
    }

    public async Task<int> MarquerVuesAsync(long jusquA, long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE alerte_non_envoyee SET vue_le = now(), vue_par = $2
            WHERE vue_le IS NULL AND id <= $1
            """);
        cmd.Parameters.AddWithValue(jusquA);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
