using Npgsql;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Infrastructure.Lecture;

public sealed class ComptesLecture(NpgsqlDataSource db) : IComptesLecture
{
    public async Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom_complet, identifiant, mot_de_passe_hash, pin_hash, role_id, actif, doit_changer_mot_de_passe
            FROM utilisateur
            WHERE identifiant = $1 AND actif
            """);
        cmd.Parameters.AddWithValue(identifiant);
        return await LireAsync(cmd, ct);
    }

    public async Task<Utilisateur?> ParIdAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom_complet, identifiant, mot_de_passe_hash, pin_hash, role_id, actif, doit_changer_mot_de_passe
            FROM utilisateur WHERE id = $1
            """);
        cmd.Parameters.AddWithValue(utilisateurId);
        return await LireAsync(cmd, ct);
    }

    public async Task ChangerMotDePasseAsync(long utilisateurId, string hash, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE utilisateur SET mot_de_passe_hash = $2, doit_changer_mot_de_passe = false WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        cmd.Parameters.AddWithValue(hash);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<Utilisateur?> LireAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Utilisateur
        {
            Id = r.GetInt64(0),
            NomComplet = r.GetString(1),
            Identifiant = r.GetString(2),
            MotDePasseHash = r.GetString(3),
            PinHash = r.IsDBNull(4) ? null : r.GetString(4),
            RoleId = r.GetInt64(5),
            Actif = r.GetBoolean(6),
            DoitChangerMotDePasse = r.GetBoolean(7)
        };
    }

    public async Task NoterConnexionAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE utilisateur SET derniere_connexion = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
