using Npgsql;
using NpgsqlTypes;

namespace Pharma.Cloud.Api.Reception;

public sealed record EtatCertificat(Guid Officine, string Serveur, DateTimeOffset Echeance, bool Revoque, bool Resiliee);

public sealed record CodeValable(Guid Officine, string Serveur, bool DejaActivee);

/// <summary>
/// Accès à la base centrale par le rôle applicatif (pharma_cloud_api, non propriétaire). Toute lecture ou écriture de données
/// reçues se fait dans une transaction où l'officine courante est posée (set_config('app.officine_id', …, true) = SET LOCAL) :
/// la Row Level Security cloisonne même une requête sans filtre.
/// </summary>
public sealed class DepotCentral(NpgsqlDataSource db)
{
    public async Task<CodeValable?> CodeValableAsync(string empreinteCode, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT officine_id, serveur, deja_activee FROM cloud_code_valable($1)");
        cmd.Parameters.AddWithValue(empreinteCode);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new CodeValable(r.GetGuid(0), r.GetString(1), r.GetBoolean(2)) : null;
    }

    public async Task<bool> ActiverAsync(string empreinteCode, Guid officine, string empreinteCertificat, string serie, DateTimeOffset echeance, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_activer($1, $2, $3, $4, $5)");
        cmd.Parameters.AddWithValue(empreinteCode);
        cmd.Parameters.AddWithValue(officine);
        cmd.Parameters.AddWithValue(empreinteCertificat);
        cmd.Parameters.AddWithValue(serie);
        cmd.Parameters.AddWithValue(echeance.UtcDateTime);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> RenouvelerAsync(string ancien, string nouveau, string serie, DateTimeOffset echeance, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_renouveler($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(ancien);
        cmd.Parameters.AddWithValue(nouveau);
        cmd.Parameters.AddWithValue(serie);
        cmd.Parameters.AddWithValue(echeance.UtcDateTime);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<EtatCertificat?> CertificatAsync(string empreinte, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT officine_id, serveur, echeance, revoque, resiliee FROM cloud_certificat($1)");
        cmd.Parameters.AddWithValue(empreinte);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new EtatCertificat(r.GetGuid(0), r.GetString(1), new DateTimeOffset(DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc)), r.GetBoolean(3), r.GetBoolean(4))
            : null;
    }

    public async Task JournaliserAsync(Guid? officine, string? certificat, string evenement, string? detail, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_journaliser($1, $2, $3, $4)");
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)officine ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)certificat ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(evenement);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)detail ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Dépose un lot contrôlé pour l'officine du certificat : (statut, dernier lot reçu).</summary>
    public async Task<(string Statut, long Dernier)> DeposerAsync(Guid officine, int epoque, long numero, string empreinte, string version,
        byte[] contenu, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT statut, dernier FROM cloud_appliquer_lot($1, $2, $3, $4, $5)", cnx, tx);
        cmd.Parameters.AddWithValue(epoque);
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(empreinte);
        cmd.Parameters.AddWithValue(version);
        cmd.Parameters.AddWithValue(contenu);
        (string, long) res;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            res = (r.GetString(0), r.GetInt64(1));
        }
        await tx.CommitAsync(ct);
        return res;
    }

    public async Task<long> DernierLotAsync(Guid officine, int epoque, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT cloud_dernier_lot($1)", cnx, tx);
        cmd.Parameters.AddWithValue(epoque);
        var n = (long)(await cmd.ExecuteScalarAsync(ct))!;
        await tx.CommitAsync(ct);
        return n;
    }

    /// <summary>Chargement complet annoncé et non terminé pour l'officine de ce certificat (script 006).</summary>
    public async Task<bool> ChargementEnCoursAsync(string empreinteCertificat, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT cloud_chargement_en_cours($1)");
        cmd.Parameters.AddWithValue(empreinteCertificat);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    /// <summary>Lot S5.3 (script 015) : contenu d'un fichier de référentiel, seulement si un ordre de cette officine le désigne (sinon null).</summary>
    public async Task<byte[]?> ReferentielAsync(Guid officine, long id, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT cloud_referentiel_contenu($1)", cnx, tx);
        cmd.Parameters.AddWithValue(id);
        var contenu = await cmd.ExecuteScalarAsync(ct) as byte[];
        await tx.CommitAsync(ct);
        return contenu;
    }

    /// <summary>Voie descendante (script 005) : éléments de version plus grande que celle appliquée par l'officine (acquittement enregistré).</summary>
    public async Task<Pharma.Shared.ChangementsDescendantsDto> DescendantsAsync(Guid officine, long versionAppliquee, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT nom_table, cle, version, donnees::text, version_max FROM cloud_descendants($1)", cnx, tx);
        cmd.Parameters.AddWithValue(versionAppliquee);
        var liste = new List<Pharma.Shared.ChangementDescendantDto>();
        long max = versionAppliquee;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                liste.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
                max = r.GetInt64(4);
            }
        }
        await tx.CommitAsync(ct);
        return new(Pharma.Shared.ChangementsDescendantsDto.ContratV1, max, liste);
    }

    /// <summary>Début d'un chargement complet (script 003) : (dernier lot attendu avant les suivants, lots abandonnés).</summary>
    public async Task<(long Dernier, long Abandonnes)> DebuterChargementAsync(Guid officine, Pharma.Shared.DebutChargementDto d, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT dernier, abandonnes FROM cloud_debut_chargement($1, $2, $3, $4, $5, $6)", cnx, tx);
        cmd.Parameters.AddWithValue(d.Numero);
        cmd.Parameters.AddWithValue(d.Epoque);
        cmd.Parameters.AddWithValue(d.Repere);
        cmd.Parameters.AddWithValue(d.ReprendreApres);
        cmd.Parameters.AddWithValue(d.Motif);
        cmd.Parameters.AddWithValue(d.Tables.ToArray());
        (long, long) res;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            res = (r.GetInt64(0), r.GetInt64(1));
        }
        await tx.CommitAsync(ct);
        return res;
    }

    /// <summary>Fin d'un chargement complet : lignes absentes de l'officine (marquées, jamais effacées), par table, et lignes vues.</summary>
    public async Task<(long Absentes, string ParTable, string Vues)> TerminerChargementAsync(Guid officine, int numero, string lignesJson, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT absentes, absentes_par_table::text, lignes_vues::text FROM cloud_fin_chargement($1, $2::jsonb)", cnx, tx)
        {
            CommandTimeout = 600
        };
        cmd.Parameters.AddWithValue(numero);
        cmd.Parameters.AddWithValue(lignesJson);
        (long, string, string) res;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await r.ReadAsync(ct);
            res = (r.GetInt64(0), r.GetString(1), r.GetString(2));
        }
        await tx.CommitAsync(ct);
        return res;
    }

    private static async Task PoserOfficineAsync(NpgsqlConnection cnx, NpgsqlTransaction tx, Guid officine, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx);
        cmd.Parameters.AddWithValue(officine.ToString("D"));
        await cmd.ExecuteScalarAsync(ct);
    }
}
