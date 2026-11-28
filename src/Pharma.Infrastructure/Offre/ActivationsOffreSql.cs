using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Offre;

namespace Pharma.Infrastructure.Offre;

/// <summary>Activations de l'offre (table activation_offre, script 172) et identité de la pharmacie (code de l'officine, NIF).</summary>
public sealed class ActivationsOffreSql(NpgsqlDataSource db) : IActivationsOffreDepot
{
    public async Task<IdentiteOffre> IdentiteAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT officine_courante(), (SELECT nif FROM pharmacie WHERE id = 1)");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        await r.ReadAsync(ct);
        return new IdentiteOffre(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    public async Task<IReadOnlyList<ActivationEnregistree>> ListerAsync(CancellationToken ct = default)
    {
        var liste = new List<ActivationEnregistree>();
        await using var cmd = db.CreateCommand("""
            SELECT a.id, a.code, a.emise_le, a.source, a.saisie_le, u.nom_complet, a.ordre_central, a.refusee_le, a.motif_refus
            FROM activation_offre a LEFT JOIN utilisateur u ON u.id = a.saisie_par
            ORDER BY a.emise_le DESC, a.id DESC
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
            liste.Add(new ActivationEnregistree(r.GetInt64(0), r.GetString(1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetString(3),
                DateTime.SpecifyKind(r.GetDateTime(4), DateTimeKind.Utc), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6),
                r.IsDBNull(7) ? null : DateTime.SpecifyKind(r.GetDateTime(7), DateTimeKind.Utc), r.IsDBNull(8) ? null : r.GetString(8)));
        return liste;
    }

    public async Task<bool> AjouterAsync(string code, DateTime emiseLeUtc, string source, long? saisiePar, Guid? ordreCentral, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO activation_offre (code, emise_le, source, saisie_par, ordre_central) VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (code) DO NOTHING
            """);
        cmd.Parameters.AddWithValue(code);
        cmd.Parameters.AddWithValue(NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(emiseLeUtc, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(source);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)saisiePar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)ordreCentral ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private static string Jour(DateOnly d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public async Task<bool> RappelNoteAsync(string etape, DateOnly fin, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "SELECT EXISTS (SELECT 1 FROM journal_audit WHERE action = 'offre_rappel' AND details ->> 'etape' = $1 AND details ->> 'fin' = $2)");
        cmd.Parameters.AddWithValue(etape);
        cmd.Parameters.AddWithValue(Jour(fin));
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<bool> NoterRappelAsync(string etape, DateOnly fin, CancellationToken ct = default)
    {
        // Verrou de transaction : deux passages simultanés ne notent (et n'envoient) jamais deux fois la même étape
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('offre_rappel'))", cnx, tx))
            await verrou.ExecuteNonQueryAsync(ct);
        await using (var deja = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM journal_audit WHERE action = 'offre_rappel' AND details ->> 'etape' = $1 AND details ->> 'fin' = $2)", cnx, tx))
        {
            deja.Parameters.AddWithValue(etape);
            deja.Parameters.AddWithValue(Jour(fin));
            if (await deja.ExecuteScalarAsync(ct) is true) return false;
        }
        await using (var note = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
            VALUES (NULL, 'offre_rappel', 'activation_offre', jsonb_build_object('etape', $1::text, 'fin', $2::text))
            """, cnx, tx))
        {
            note.Parameters.AddWithValue(etape);
            note.Parameters.AddWithValue(Jour(fin));
            await note.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Plus grande des deux dates du journal fiscal (chaîné et scellé : une ligne effacée ou modifiée se voit au contrôle d'intégrité) : la plus
    /// grande date des faits (index sur survenu_le) et la date d'enregistrement de la dernière ligne (clé primaire), toutes deux posées par la base.
    /// </summary>
    public async Task<DateTime?> DerniereDateFiscaleAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT GREATEST((SELECT max(survenu_le) FROM journal_vente), (SELECT enregistre_le FROM journal_vente ORDER BY id DESC LIMIT 1))
            """);
        return await cmd.ExecuteScalarAsync(ct) is DateTime d ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;
    }

    public Task<bool> SignalerHorlogeReculeeAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default) =>
        SignalerHorlogeAsync(OffreCourante.ActionHorlogeReculee, plancherUtc, serveurUtc, ct);

    public Task<bool> SignalerHorlogeAvanceAsync(DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct = default) =>
        SignalerHorlogeAsync(OffreCourante.ActionHorlogeAvance, plancherUtc, serveurUtc, ct);

    private async Task<bool> SignalerHorlogeAsync(string action, DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct)
    {
        // Jamais bloquant : base en lecture seule (serveur écarté, 2e serveur en veille) → rien n'est noté, le message reste dans Mon offre
        try { return await NoterHorlogeAsync(action, plancherUtc, serveurUtc, ct); }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction) { return false; }
    }

    private static string Iso(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    private async Task<bool> NoterHorlogeAsync(string action, DateTime plancherUtc, DateTime serveurUtc, CancellationToken ct)
    {
        // Une seule ligne par action et par date plancher (à la seconde) ; verrou de transaction : deux évaluations simultanées n'en écrivent qu'une
        var plancher = Iso(plancherUtc);
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext($1))", cnx, tx))
        {
            verrou.Parameters.AddWithValue(action);
            await verrou.ExecuteNonQueryAsync(ct);
        }
        await using (var deja = new NpgsqlCommand(
            "SELECT EXISTS (SELECT 1 FROM journal_audit WHERE action = $1 AND details ->> 'plancher' = $2)", cnx, tx))
        {
            deja.Parameters.AddWithValue(action);
            deja.Parameters.AddWithValue(plancher);
            if (await deja.ExecuteScalarAsync(ct) is true) return false;
        }
        await using (var note = new NpgsqlCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, details)
            VALUES (NULL, $1, 'activation_offre', jsonb_build_object('plancher', $2::text, 'serveur', $3::text))
            """, cnx, tx))
        {
            note.Parameters.AddWithValue(action);
            note.Parameters.AddWithValue(plancher);
            note.Parameters.AddWithValue(Iso(serveurUtc));
            await note.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>
    /// Une seule instruction (atomique) : marque levée seulement si elle est posée, et la ligne de journal (ancienne date et ancien motif)
    /// écrite avec elle. Base en lecture seule : rien n'est fait.
    /// </summary>
    public async Task<bool> LeverRefusAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH avant AS (
                SELECT id, refusee_le, motif_refus FROM activation_offre WHERE id = $1 AND refusee_le IS NOT NULL FOR UPDATE
            ), levee AS (
                UPDATE activation_offre a SET refusee_le = NULL, motif_refus = NULL FROM avant WHERE a.id = avant.id
                RETURNING avant.id, avant.refusee_le, avant.motif_refus
            )
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details)
            SELECT NULL, $2, 'activation_offre', levee.id,
                   jsonb_build_object('refusee_le', to_char(levee.refusee_le AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'), 'motif', levee.motif_refus)
            FROM levee
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(OffreCourante.ActionRefusLeve);
        try { return await cmd.ExecuteNonQueryAsync(ct) == 1; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction) { return false; }
    }

    /// <summary>Première constatation seulement : refusee_le et motif posés, puis une ligne au journal des actions (sans utilisateur).</summary>
    public async Task<bool> SignalerRefusAsync(long id, string motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT activation_offre_signaler_refus($1, $2)");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(motif.Length > 300 ? motif[..300] : motif);
        // Base en lecture seule (2e serveur en veille) : rien à noter ici, le serveur principal le fera ; l'offre se calcule quand même
        try { return await cmd.ExecuteScalarAsync(ct) is true; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ReadOnlySqlTransaction) { return false; }
    }
}
