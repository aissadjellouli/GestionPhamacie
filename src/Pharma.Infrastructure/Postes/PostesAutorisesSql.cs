using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Commun;
using Pharma.Application.Postes;

namespace Pharma.Infrastructure.Postes;

/// <summary>Liste des postes autorisés (script 167) : tables poste_autorise et parametre_postes. Sans état : enregistrable en singleton.</summary>
public sealed class PostesAutorisesSql(NpgsqlDataSource db) : IPostesAutorisesDepot
{
    private const string Colonnes = """
        id, nom, statut, generation, cree_le, enrole_le, derniere_preuve_le, ordinateur, empreinte_cle, code_expire_le, revoque_le, motif_revocation, cree_par
        """;

    public async Task<ReglagesPostes> ReglagesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT mode, maximum_postes, duree_code_minutes FROM parametre_postes WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        // Ligne absente : valeurs par défaut du schéma
        return await r.ReadAsync(ct) ? new ReglagesPostes(r.GetString(0), r.GetInt32(1), r.GetInt32(2)) : ReglagesPostes.ParDefaut;
    }

    public async Task EnregistrerReglagesAsync(ReglagesPostes reglages, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO parametre_postes (id, mode, maximum_postes, duree_code_minutes) VALUES (1, $1, $2, $3)
            ON CONFLICT (id) DO UPDATE SET mode = EXCLUDED.mode, maximum_postes = EXCLUDED.maximum_postes, duree_code_minutes = EXCLUDED.duree_code_minutes
            """);
        cmd.Parameters.AddWithValue(reglages.Mode);
        cmd.Parameters.AddWithValue(reglages.MaximumPostes);
        cmd.Parameters.AddWithValue(reglages.DureeCodeMinutes);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<PosteCle?> LireCleAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT id, nom, statut, cle_publique, generation FROM poste_autorise WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct)
            ? new PosteCle(r.GetInt64(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3), r.GetInt32(4))
            : null;
    }

    public async Task<IReadOnlySet<string>> NomsReservesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT lower(nom) FROM poste_autorise WHERE statut <> 'revoque'");
        var noms = new HashSet<string>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) noms.Add(r.GetString(0).Trim());
        return noms;
    }

    public async Task<Page<PosteAutoriseLu>> ListerAsync(string? statut, string? recherche, DemandePage page, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"""
            SELECT {Colonnes}, count(*) OVER ()
            FROM poste_autorise
            WHERE ($1::text IS NULL OR statut = $1) AND ($2::text IS NULL OR nom ILIKE '%' || $2 || '%' OR ordinateur ILIKE '%' || $2 || '%')
            ORDER BY CASE statut WHEN 'actif' THEN 0 WHEN 'en_attente' THEN 1 ELSE 2 END, lower(nom), id
            LIMIT $3 OFFSET $4
            """);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)statut ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)Echapper(recherche) ?? DBNull.Value });
        cmd.Parameters.AddWithValue(page.Taille);
        cmd.Parameters.AddWithValue(page.Decalage);
        var lignes = new List<PosteAutoriseLu>();
        var total = 0;
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            lignes.Add(Lire(r));
            total = (int)r.GetInt64(13);
        }
        return new Page<PosteAutoriseLu>(lignes, total, page.Page, page.Taille);
    }

    public async Task<PosteAutoriseLu?> LireAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand($"SELECT {Colonnes} FROM poste_autorise WHERE id = $1");
        cmd.Parameters.AddWithValue(id);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? Lire(r) : null;
    }

    public async Task<int> NombreNonRevoquesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT count(*)::int FROM poste_autorise WHERE statut <> 'revoque'");
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<CreationPoste> CreerAsync(string nom, byte[] codeHache, DateTime expireLe, long creePar, int maximum, CancellationToken ct = default)
    {
        await using var cx = await db.OpenConnectionAsync(ct);
        await using var tx = await cx.BeginTransactionAsync(ct);
        // Une création à la fois : sans verrou, deux autorisations simultanées comptaient chacune les mêmes postes et dépassaient le maximum
        await using (var verrou = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtext('poste_autorise_creation'))", cx, tx))
            await verrou.ExecuteNonQueryAsync(ct);
        // Compté après le verrou, par une nouvelle requête (nouvel instantané) : voit le poste créé par l'autorisation précédente
        await using (var compte = new NpgsqlCommand("SELECT count(*)::int FROM poste_autorise WHERE statut <> 'revoque'", cx, tx))
            if ((int)(await compte.ExecuteScalarAsync(ct))! >= maximum)
                return new CreationPoste(null, MaximumAtteint: true);
        await using var cmd = new NpgsqlCommand("""
            INSERT INTO poste_autorise (nom, code_hache, code_expire_le, cree_par) VALUES ($1, $2, $3, $4)
            ON CONFLICT (lower(nom)) WHERE statut <> 'revoque' DO NOTHING
            RETURNING id
            """, cx, tx);
        cmd.Parameters.AddWithValue(nom);
        cmd.Parameters.AddWithValue(codeHache);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(expireLe, DateTimeKind.Utc));
        cmd.Parameters.AddWithValue(creePar);
        var id = await cmd.ExecuteScalarAsync(ct) is long v ? v : (long?)null;
        await tx.CommitAsync(ct);
        return new CreationPoste(id);
    }

    public async Task<bool> NouveauCodeAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand(
            "UPDATE poste_autorise SET code_hache = $2, code_expire_le = $3, code_essais = 0 WHERE id = $1 AND statut = 'en_attente'");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(codeHache);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(expireLe, DateTimeKind.Utc));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool> RemplacerAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE poste_autorise
            SET statut = 'en_attente', cle_publique = NULL, empreinte_cle = NULL, enrole_le = NULL, ordinateur = NULL, generation = generation + 1,
                code_hache = $2, code_expire_le = $3, code_essais = 0
            WHERE id = $1 AND statut = 'actif'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(codeHache);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(expireLe, DateTimeKind.Utc));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<bool?> RenommerAsync(long id, string nom, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE poste_autorise SET nom = $2 WHERE id = $1 AND statut <> 'revoque'");
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(nom);
        try { return await cmd.ExecuteNonQueryAsync(ct) == 1 ? true : null; }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation) { return false; }
    }

    public async Task<bool> RevoquerAsync(long id, long revoquePar, string motif, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            UPDATE poste_autorise SET statut = 'revoque', revoque_le = now(), revoque_par = $2, motif_revocation = $3, code_hache = NULL, code_expire_le = NULL
            WHERE id = $1 AND statut <> 'revoque'
            """);
        cmd.Parameters.AddWithValue(id);
        cmd.Parameters.AddWithValue(revoquePar);
        cmd.Parameters.AddWithValue(motif);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<EnrolementLu?> EnrolerAsync(byte[] codeHache, byte[] clePublique, string empreinte, string? ordinateur, int essaisMax, CancellationToken ct = default)
    {
        // Une seule requête : le code ne sert qu'une fois, même avec deux enrôlements simultanés
        await using var cmd = db.CreateCommand("""
            UPDATE poste_autorise
            SET statut = 'actif', cle_publique = $2, empreinte_cle = $3, ordinateur = $4, enrole_le = now(), code_hache = NULL, code_expire_le = NULL, code_essais = 0
            WHERE statut = 'en_attente' AND code_hache = $1 AND code_expire_le > now() AND code_essais < $5
            RETURNING id, nom, cree_par
            """);
        cmd.Parameters.AddWithValue(codeHache);
        cmd.Parameters.AddWithValue(clePublique);
        cmd.Parameters.AddWithValue(empreinte);
        cmd.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)ordinateur ?? DBNull.Value });
        cmd.Parameters.AddWithValue(essaisMax);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new EnrolementLu(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2)) : null;
    }

    public async Task<int> NoterEchecCodeAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            WITH m AS (UPDATE poste_autorise SET code_essais = LEAST(code_essais + 1, 1000) WHERE statut = 'en_attente' RETURNING code_essais)
            SELECT coalesce(max(code_essais), 0)::int FROM m
            """);
        return (int)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<bool> PosteOccupeAsync(string nom, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM session_caisse WHERE lower(poste) = lower($1) AND fermee_le IS NULL)
                OR EXISTS (SELECT 1 FROM panier_attente WHERE lower(poste) = lower($1))
                OR EXISTS (SELECT 1 FROM reception_attente WHERE lower(poste) = lower($1))
            """);
        cmd.Parameters.AddWithValue(nom);
        return await cmd.ExecuteScalarAsync(ct) is true;
    }

    public async Task<IReadOnlyList<NomSansPreuve>> NomsSansPreuveAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT c.poste, max(c.cree_le)
            FROM connexion_employe c
            WHERE c.cree_le > now() - interval '30 days' AND c.poste IS NOT NULL AND btrim(c.poste) <> ''
              AND NOT EXISTS (SELECT 1 FROM poste_autorise p WHERE p.statut <> 'revoque' AND lower(p.nom) = lower(btrim(c.poste)))
            GROUP BY c.poste
            ORDER BY max(c.cree_le) DESC
            LIMIT 50
            """);
        var noms = new List<NomSansPreuve>();
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) noms.Add(new NomSansPreuve(r.GetString(0), r.GetFieldValue<DateTime>(1)));
        return noms;
    }

    public async Task NoterPreuveAsync(long id, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE poste_autorise SET derniere_preuve_le = now() WHERE id = $1 AND statut = 'actif'");
        cmd.Parameters.AddWithValue(id);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static PosteAutoriseLu Lire(NpgsqlDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetFieldValue<DateTime>(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTime>(5), r.IsDBNull(6) ? null : r.GetFieldValue<DateTime>(6), r.IsDBNull(7) ? null : r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8), r.IsDBNull(9) ? null : r.GetFieldValue<DateTime>(9), r.IsDBNull(10) ? null : r.GetFieldValue<DateTime>(10),
        r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetInt64(12));

    // Recherche ILIKE : les caractères % et _ saisis sont cherchés tels quels
    private static string? Echapper(string? texte) =>
        string.IsNullOrWhiteSpace(texte) ? null : texte.Trim().Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
}
