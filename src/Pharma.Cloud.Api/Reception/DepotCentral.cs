using Npgsql;
using NpgsqlTypes;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Reception;

public sealed record EtatCertificat(Guid Officine, string Serveur, DateTimeOffset Echeance, bool Revoque, bool Resiliee);

public sealed record CodeValable(Guid Officine, string Serveur, bool DejaActivee);

/// <summary>
/// Accès à la base centrale par le rôle applicatif (pharma_cloud_api, non propriétaire). Toute lecture ou écriture de données
/// reçues se fait dans une transaction où l'officine courante est posée (set_config('app.officine_id', …, true) = SET LOCAL) :
/// la Row Level Security cloisonne même une requête sans filtre.
/// </summary>
public sealed class DepotCentral(NpgsqlDataSource db, Microsoft.Extensions.Logging.ILogger<DepotCentral>? journal = null, TimeProvider? temps = null)
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
            await LigneAttendueAsync(r, "cloud_appliquer_lot", ct);
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

    /// <summary>Dernier lot reçu d'une époque et plus grande époque reçue de l'officine (cloud_epoque_max, script 020), en une lecture.</summary>
    public async Task<(long Dernier, int EpoqueMax)> EtatAsync(Guid officine, int epoque, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT cloud_dernier_lot($1), cloud_epoque_max()", cnx, tx);
        cmd.Parameters.AddWithValue(epoque);
        (long, int) res;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await LigneAttendueAsync(r, "cloud_dernier_lot", ct);
            res = (r.GetInt64(0), r.GetInt32(1));
        }
        await tx.CommitAsync(ct);
        return res;
    }

    /// <summary>
    /// Lot S6.1 (script 020) : plus grand id reçu de cette officine par table miroir à clé « id » entière, toutes époques, lignes absentes
    /// et archivées comprises. Aucune donnée de ligne ; une table sans ligne n'apparaît pas.
    /// </summary>
    public async Task<Dictionary<string, long>> MaximumsAsync(Guid officine, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand("SELECT nom_table, maximum FROM cloud_maximums_officine()", cnx, tx) { CommandTimeout = 120 };
        var maximums = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct)) maximums[r.GetString(0)] = r.GetInt64(1);
        }
        // Lot S6.5.4 (script 024, S6.5-D6 a) : plus grand n° de ticket reçu, sous une entrée réservée qui n'est pas un nom de table
        // (une officine antérieure l'ignore : seuls les noms de table valides sont retenus pour l'avance des séquences)
        await using (var ticket = new NpgsqlCommand("SELECT cloud_dernier_ticket()", cnx, tx) { CommandTimeout = 120 })
            if (await ticket.ExecuteScalarAsync(ct) is long t && t > 0) maximums[MaximumsCentralDto.CleTicket] = t;
        await tx.CommitAsync(ct);
        return maximums;
    }

    /// <summary>
    /// Lot S6.5.3 (script 022) : déclaration d'un changement d'époque par l'officine du certificat (cloud_declarer_changement_epoque,
    /// SECURITY DEFINER). Renvoi identique : la déclaration existante est rendue. PostgresException 22023 (contenu refusé) ou
    /// 23505 (autre contenu pour la même époque) remontée à l'appelant.
    /// </summary>
    public async Task<ReponseDeclarationEpoqueDto> DeclarerEpoqueAsync(Guid officine, DeclarationEpoqueDto d, CancellationToken ct)
    {
        var fins = System.Text.Json.JsonSerializer.Serialize(d.FinsChaine.Select(f => new Dictionary<string, object?>
        {
            ["journal"] = f.Journal, ["cle_chaine"] = f.CleChaine, ["rang"] = f.Rang, ["id"] = f.Id, ["hash"] = f.Hash
        }));
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT changement_id, recu_le, deja_declare, avant_premier_lot FROM cloud_declarer_changement_epoque($1, $2, $3, $4, $5, $6, $7, $8::jsonb)", cnx, tx);
        cmd.Parameters.AddWithValue(d.AncienneEpoque);
        cmd.Parameters.AddWithValue(d.NouvelleEpoque);
        cmd.Parameters.AddWithValue(d.DernierLotLocal);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.DernierLotCentral ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.DetecteLe?.UtcDateTime ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = d.DecideLe.UtcDateTime, NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)d.TitulaireId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = fins, NpgsqlDbType = NpgsqlDbType.Text });
        ReponseDeclarationEpoqueDto rep;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            await LigneAttendueAsync(r, "cloud_declarer_changement_epoque", ct);
            rep = new ReponseDeclarationEpoqueDto(r.GetInt64(0), r.GetFieldValue<DateTimeOffset>(1), r.GetBoolean(2), r.GetBoolean(3));
        }
        await tx.CommitAsync(ct);
        return rep;
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

    /// <summary>
    /// Voie descendante (script 005) : éléments de version plus grande que celle appliquée par l'officine (acquittement enregistré),
    /// par ordre de version et BORNÉS (revue du 2026-11-22) : au plus MaxElements éléments et OctetsDonneesParReponse octets de données
    /// (au moins un élément) ; les suivants partent au passage suivant. Sans borne, une file de plus de 1 Mio faisait échouer la lecture
    /// de chaque réponse côté officine (plus aucun lot accusé).
    /// </summary>
    public async Task<Pharma.Shared.ChangementsDescendantsDto> DescendantsAsync(Guid officine, long versionAppliquee, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await PoserOfficineAsync(cnx, tx, officine, ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT nom_table, cle, version, donnees::text, version_max FROM cloud_descendants($1) ORDER BY version LIMIT $2", cnx, tx);
        cmd.Parameters.AddWithValue(versionAppliquee);
        cmd.Parameters.AddWithValue(Pharma.Shared.ChangementsDescendantsDto.MaxElements);
        var lus = new List<Pharma.Shared.ChangementDescendantDto>();
        long max = versionAppliquee;
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                lus.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3)));
                max = r.GetInt64(4);
            }
        }
        var liste = Borner(lus, journal);
        await tx.CommitAsync(ct);
        return Dater(new(Pharma.Shared.ChangementsDescendantsDto.ContratV1, max, liste), temps ?? TimeProvider.System);
    }

    /// <summary>
    /// Date du serveur central ajoutée à la réponse de la voie descendante (échange léger et accusé d'un lot ; champ facultatif, 2026-11-27) :
    /// l'officine s'en sert comme date plancher de son offre. Aucune donnée d'officine : la seule horloge du central.
    /// </summary>
    public static Pharma.Shared.ChangementsDescendantsDto Dater(Pharma.Shared.ChangementsDescendantsDto d, TimeProvider temps) =>
        d with { DateCentrale = temps.GetUtcNow() };

    /// <summary>
    /// Ligne unique attendue d'une fonction SQL (revue du 2026-11-22) : aucune ligne (filtre de cloisonnement, fonction modifiée) donne
    /// une <see cref="ReponseCentraleVideException"/> nommant la fonction, au lieu d'une lecture sur une ligne absente.
    /// </summary>
    public static async Task LigneAttendueAsync(NpgsqlDataReader r, string fonction, CancellationToken ct)
    {
        if (!await r.ReadAsync(ct)) throw new ReponseCentraleVideException(fonction);
    }

    /// <summary>Options JSON des réponses de l'API (valeurs par défaut des API minimales).</summary>
    public static readonly System.Text.Json.JsonSerializerOptions OptionsReponse = new(System.Text.Json.JsonSerializerDefaults.Web);

    /// <summary>
    /// Éléments d'une réponse descendante, dans l'ordre des versions : au plus MaxElements et OctetsDonneesParReponse octets écrits,
    /// toujours au moins le premier. Un élément dont les données dépassent MaxDonnees (la taille lisible par l'officine, même mesure
    /// qu'elle) n'est jamais écrit : il part SANS données (version, table et clé seules) et est journalisé ; l'officine l'écarte, acquitte
    /// sa version et passe au suivant, sans que la réponse puisse dépasser la limite de lecture.
    /// </summary>
    public static List<Pharma.Shared.ChangementDescendantDto> Borner(IEnumerable<Pharma.Shared.ChangementDescendantDto> tries,
        Microsoft.Extensions.Logging.ILogger? journal = null)
    {
        var liste = new List<Pharma.Shared.ChangementDescendantDto>();
        var octets = 0L;
        foreach (var element in tries)
        {
            var c = element;
            if (c.DonneesJson is { Length: > Pharma.Shared.ChangementsDescendantsDto.MaxDonnees })
            {
                journal?.LogWarning("Élément descendant {Table} version {Version} écarté côté central : données de {Taille} caractères (maximum {Max})",
                    c.Table, c.Version, c.DonneesJson.Length, Pharma.Shared.ChangementsDescendantsDto.MaxDonnees);
                c = c with { DonneesJson = "" };
            }
            // Taille réelle dans la réponse (mêmes options que les réponses JSON de l'API : échappements compris)
            octets += System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(c, OptionsReponse).Length + 1;
            if (liste.Count >= Pharma.Shared.ChangementsDescendantsDto.MaxElements
                || (liste.Count > 0 && octets > Pharma.Shared.ChangementsDescendantsDto.OctetsDonneesParReponse)) break;
            liste.Add(c);
        }
        return liste;
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
            await LigneAttendueAsync(r, "cloud_debut_chargement", ct);
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
            await LigneAttendueAsync(r, "cloud_fin_chargement", ct);
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

/// <summary>Fonction SQL du central qui n'a rendu aucune ligne alors qu'une était attendue (anomalie de la base, jamais une donnée).</summary>
public sealed class ReponseCentraleVideException(string fonction) : Exception($"Aucune ligne rendue par {fonction}.")
{
    public string Fonction { get; } = fonction;
}
