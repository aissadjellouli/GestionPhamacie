using Npgsql;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Infrastructure.Lecture;

public sealed class ComptesLecture(NpgsqlDataSource db) : IComptesLecture
{
    public async Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, nom_complet, identifiant, mot_de_passe_hash, pin_hash, role_id, actif
            FROM utilisateur
            WHERE identifiant = $1 AND actif
            """);
        cmd.Parameters.AddWithValue(identifiant);
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
            Actif = r.GetBoolean(6)
        };
    }

    public async Task NoterConnexionAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE utilisateur SET derniere_connexion = now() WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
