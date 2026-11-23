using Npgsql;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Comptes;

/// <summary>
/// Lot S7.2b (script central 033) : code de rattachement demandé par le poste de l'officine (POST /api/v1/rattachement, certificat
/// client + jeton, comme l'échange). Officine = celle du certificat (posée par l'API puis revérifiée par la base), jamais un champ de
/// la demande. Même tirage que la console (ComptesDistants.TirerCodeRattachement) et mêmes règles en base
/// (cloud_code_rattachement_enregistrer). Compte inconnu, non titulaire ou désactivé : même réponse neutre ; 5 demandes par heure et
/// par officine. Le code n'est jamais écrit dans un journal.
/// </summary>
public static class RattachementPoste
{
    public const string RefusNeutre =
        "Code non délivré : vérifiez l'identifiant du compte distant (compte titulaire actif des écrans du serveur central).";
    public const string RefusLimite = "Trop de codes demandés pour cette officine (5 par heure au plus) : réessayez plus tard.";

    public const string Ok = "ok", Refus = "refus", TropDeCodes = "trop_de_codes";

    /// <summary>Statut rendu par la base → (code HTTP, motif) ; null pour « ok ». Tout statut inconnu reçoit la réponse neutre.</summary>
    public static (int Code, string Motif)? Reponse(string statut) => statut switch
    {
        Ok => null,
        TropDeCodes => (429, RefusLimite),
        _ => (422, RefusNeutre),
    };

    /// <summary>
    /// Demande du poste : transaction où l'officine du certificat est posée, puis cloud_poste_code_rattachement (SECURITY DEFINER,
    /// rôle pharma_cloud_api). Rend (statut, code en clair si délivré, échéance).
    /// </summary>
    public static async Task<(string Statut, string? Code, DateTimeOffset? Expire)> DemanderAsync(NpgsqlDataSource db, Guid officine,
        string empreinteCertificat, string? compte, CancellationToken ct)
    {
        var (code, empreinte) = ComptesDistants.TirerCodeRattachement();
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var poser = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
        {
            poser.Parameters.AddWithValue(officine.ToString("D"));
            await poser.ExecuteScalarAsync(ct);
        }
        string statut;
        DateTimeOffset? expire;
        await using (var cmd = new NpgsqlCommand("SELECT statut, expire_le FROM cloud_poste_code_rattachement($1, $2, $3)", cnx, tx))
        {
            cmd.Parameters.AddWithValue(empreinteCertificat);
            cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)(compte is { Length: <= 200 } ? compte : null) ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            cmd.Parameters.AddWithValue(empreinte);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            await Reception.DepotCentral.LigneAttendueAsync(r, "cloud_poste_code_rattachement", ct);
            statut = r.GetString(0);
            expire = r.IsDBNull(1) ? null : r.GetFieldValue<DateTimeOffset>(1);
        }
        await tx.CommitAsync(ct);
        return statut == Ok && expire is not null ? (statut, code, expire) : (statut, null, null);
    }
}
