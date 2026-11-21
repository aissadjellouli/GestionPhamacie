using Npgsql;
using NpgsqlTypes;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Comptes;

/// <summary>
/// Lot S7.3 (script central 034) : le poste de l'officine voit les comptes distants rattachés, leur donne ou retire des droits
/// (dont « ordres », jamais accordé ailleurs) et retire un compte (effet immédiat, ordres ouverts de ce compte annulés).
/// Routes /api/v1/acces-distants (certificat client + jeton, comme l'échange). Officine = celle du certificat, posée ici puis
/// revérifiée par la base (cloud_poste_officine) ; jamais un champ de la demande. Le mot de passe du titulaire est vérifié par
/// l'API locale avant l'appel.
/// </summary>
public static class AccesDistantsPoste
{
    public const string RefusInconnu = "Compte distant non rattaché à cette officine (ou déjà retiré).";
    public const string RefusDroits = "Droits attendus : au moins un parmi consulter, journal_acces, ordres.";

    /// <summary>Statut rendu par la base → (code HTTP, motif) ; null pour « ok ».</summary>
    public static (int Code, string Motif)? Reponse(string statut) => statut switch
    {
        "ok" => null,
        "droits_invalides" => (422, RefusDroits),
        _ => (422, RefusInconnu),
    };

    public static Task<List<CompteDistantRattacheDto>> ListerAsync(NpgsqlDataSource db, Guid officine, string certificat, CancellationToken ct) =>
        DansOfficineAsync(db, officine, async (cnx, tx) =>
        {
            await using var cmd = new NpgsqlCommand("SELECT identifiant, nom, droits, rattache_le, actif FROM cloud_poste_comptes_distants($1)", cnx, tx);
            cmd.Parameters.AddWithValue(certificat);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            var liste = new List<CompteDistantRattacheDto>();
            while (await r.ReadAsync(ct))
                liste.Add(new CompteDistantRattacheDto(r.GetString(0), r.GetString(1), r.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries),
                    r.GetFieldValue<DateTimeOffset>(3), r.GetBoolean(4)));
            return liste;
        }, ct);

    public static Task<(string Statut, int OrdresAnnules)> DroitsAsync(NpgsqlDataSource db, Guid officine, string certificat, string? compte,
        IReadOnlyList<string>? droits, CancellationToken ct) =>
        StatutAsync(db, officine, "SELECT statut, ordres_annules FROM cloud_poste_compte_droits($1, $2, $3)", certificat, compte,
            new NpgsqlParameter { Value = (droits ?? []).Take(10).Select(d => d ?? "").ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }, ct);

    public static Task<(string Statut, int OrdresAnnules)> RetirerAsync(NpgsqlDataSource db, Guid officine, string certificat, string? compte, CancellationToken ct) =>
        StatutAsync(db, officine, "SELECT statut, ordres_annules FROM cloud_poste_compte_retirer($1, $2)", certificat, compte, null, ct);

    private static Task<(string, int)> StatutAsync(NpgsqlDataSource db, Guid officine, string sql, string certificat, string? compte,
        NpgsqlParameter? supplement, CancellationToken ct) =>
        DansOfficineAsync(db, officine, async (cnx, tx) =>
        {
            await using var cmd = new NpgsqlCommand(sql, cnx, tx);
            cmd.Parameters.AddWithValue(certificat);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)(compte is { Length: <= 200 } ? compte : null) ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            if (supplement is not null) cmd.Parameters.Add(supplement);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await r.ReadAsync(ct);
            return (r.GetString(0), r.GetInt32(1));
        }, ct);

    /// <summary>Transaction où l'officine du certificat est posée (la base la revérifie avec le certificat présenté).</summary>
    private static async Task<T> DansOfficineAsync<T>(NpgsqlDataSource db, Guid officine, Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> travail, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var poser = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
        {
            poser.Parameters.AddWithValue(officine.ToString("D"));
            await poser.ExecuteScalarAsync(ct);
        }
        var resultat = await travail(cnx, tx);
        await tx.CommitAsync(ct);
        return resultat;
    }
}
