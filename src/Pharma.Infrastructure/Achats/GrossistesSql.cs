using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Achats;

namespace Pharma.Infrastructure.Achats;

/// <summary>Création et modification des grossistes (la dette, elle, ne bouge qu'avec les réceptions, avoirs et règlements).</summary>
public sealed class GrossistesSql(NpgsqlDataSource db) : IGrossistes
{
    public async Task<long> EnregistrerAsync(FicheGrossiste f, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(f.Id is null
            ? """
              INSERT INTO fournisseur (raison_sociale, telephone, email, adresse, nif, rc, delai_reprise_jours, actif)
              VALUES ($2, $3, $4, $5, $6, $7, $8, $9) RETURNING id
              """
            : """
              UPDATE fournisseur SET raison_sociale = $2, telephone = $3, email = $4, adresse = $5, nif = $6, rc = $7,
                                     delai_reprise_jours = $8, actif = $9
              WHERE id = $1 RETURNING id
              """);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.Id ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(f.RaisonSociale);
        foreach (var v in new[] { f.Telephone, f.Email, f.Adresse, f.Nif, f.Rc })
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)f.DelaiRepriseJours ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        cmd.Parameters.AddWithValue(f.Actif);
        return await cmd.ExecuteScalarAsync(ct) is long id ? id : throw new Pharma.Domain.Commun.RegleMetierException("Grossiste introuvable.");
    }

    public async Task<bool> RaisonSocialeExisteAsync(string raisonSociale, long? sauf, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT EXISTS (SELECT 1 FROM fournisseur WHERE lower(raison_sociale) = lower($1) AND id <> COALESCE($2, 0))");
        cmd.Parameters.AddWithValue(raisonSociale);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)sauf ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }
}
