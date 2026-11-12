using Npgsql;
using Pharma.Application.Securite;

namespace Pharma.Infrastructure.Securite;

/// <summary>État du compte relu à chaque appel authentifié (063_revocation_connexions.sql).</summary>
public sealed class EtatConnexionsSql(NpgsqlDataSource db) : IEtatConnexions
{
    public async Task<EtatConnexion?> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT actif, doit_changer_mot_de_passe, jetons_revoques_le FROM utilisateur WHERE id = $1");
        cmd.Parameters.AddWithValue(utilisateurId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new EtatConnexion(r.GetBoolean(0), r.GetBoolean(1), r.IsDBNull(2) ? null : r.GetDateTime(2)) : null;
    }
}
