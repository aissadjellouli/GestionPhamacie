using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Integrite;

namespace Pharma.Infrastructure.Securite;

/// <summary>Tables et fonctions de la brique d'intégrité (script 111) : registre, vérification SQL, sceaux, vérifications.</summary>
public sealed class IntegriteSql(NpgsqlDataSource db) : IIntegriteDepot
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<JournalChaine>> JournauxAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT journal, libelle, algorithme, active_le FROM chaine_integrite ORDER BY libelle");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<JournalChaine>();
        while (await r.ReadAsync(ct)) liste.Add(new JournalChaine(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDateTime(3)));
        return liste;
    }

    public async Task<IReadOnlyList<ResultatChaine>> VerifierChaineAsync(string journal, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT cle_chaine, lignes, dernier_rang, dernier_hash, rang_rupture, id_rupture, motif, attendu, trouve
            FROM integrite_verifier($1)
            """);
        cmd.Parameters.AddWithValue(journal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<ResultatChaine>();
        while (await r.ReadAsync(ct))
            liste.Add(new ResultatChaine(r.GetString(0), r.GetInt64(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetFieldValue<byte[]>(3),
                r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5), Texte(r, 6), Texte(r, 7), Texte(r, 8)));
        return liste;
    }

    public async Task<MaillonChaine> DernierMaillonAsync(string journal, string cleChaine, CancellationToken ct = default)
    {
        var table = await TableAsync(journal, ct);
        await using var cmd = db.CreateCommand($"""
            SELECT t.rang, t.id, t.hash FROM {table} t WHERE t.cle_chaine = $1 ORDER BY t.rang DESC LIMIT 1
            """);
        cmd.Parameters.AddWithValue(cleChaine);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            if (await r.ReadAsync(ct)) return new MaillonChaine(r.GetInt64(0), r.GetInt64(1), r.GetFieldValue<byte[]>(2));
        // Journal vide : rang 0 et empreinte d'origine
        await using var origine = db.CreateCommand("SELECT integrite_origine(algorithme) FROM chaine_integrite WHERE journal = $1");
        origine.Parameters.AddWithValue(journal);
        return new MaillonChaine(0, null, (byte[])(await origine.ExecuteScalarAsync(ct))!);
    }

    public async Task<byte[]?> HashAuRangAsync(string journal, string cleChaine, long rang, CancellationToken ct = default)
    {
        var table = await TableAsync(journal, ct);
        await using var cmd = db.CreateCommand($"SELECT t.hash FROM {table} t WHERE t.cle_chaine = $1 AND t.rang = $2");
        cmd.Parameters.AddWithValue(cleChaine);
        cmd.Parameters.AddWithValue(rang);
        return await cmd.ExecuteScalarAsync(ct) as byte[];
    }

    public async Task<long> EnregistrerSceauAsync(NouveauSceau s, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO sceau_integrite (journal, cle_chaine, motif, periode_du, periode_au, scelle_le, dernier_rang, dernier_id, dernier_hash,
                                         algorithme, empreinte_cle, sceau, auteur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13) RETURNING id
            """);
        cmd.Parameters.AddWithValue(s.Journal);
        cmd.Parameters.AddWithValue(s.CleChaine);
        cmd.Parameters.AddWithValue(s.Motif);
        cmd.Parameters.AddWithValue(s.PeriodeDu);
        cmd.Parameters.AddWithValue(s.PeriodeAu);
        cmd.Parameters.Add(new NpgsqlParameter { Value = DateTime.SpecifyKind(s.ScelleLeUtc, DateTimeKind.Utc), NpgsqlDbType = NpgsqlDbType.TimestampTz });
        cmd.Parameters.AddWithValue(s.DernierRang);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.DernierId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.AddWithValue(s.DernierHash);
        cmd.Parameters.AddWithValue(s.Algorithme);
        cmd.Parameters.AddWithValue(s.EmpreinteCle);
        cmd.Parameters.AddWithValue(s.Sceau);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)s.AuteurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<SceauEnregistre>> SceauxAsync(string journal, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT id, journal, cle_chaine, motif, periode_du, periode_au, scelle_le, dernier_rang, dernier_id, dernier_hash,
                   algorithme, empreinte_cle, sceau, auteur_id
            FROM sceau_integrite WHERE journal = $1 ORDER BY id
            """);
        cmd.Parameters.AddWithValue(journal);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<SceauEnregistre>();
        while (await r.ReadAsync(ct))
            liste.Add(new SceauEnregistre(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4),
                r.GetFieldValue<DateOnly>(5), DateTime.SpecifyKind(r.GetDateTime(6), DateTimeKind.Utc), r.GetInt64(7),
                r.IsDBNull(8) ? null : r.GetInt64(8), r.GetFieldValue<byte[]>(9), r.GetString(10), r.GetString(11), r.GetFieldValue<byte[]>(12),
                r.IsDBNull(13) ? null : r.GetInt64(13)));
        return liste;
    }

    public async Task EnregistrerVerificationAsync(NouvelleVerification v, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO verification_integrite (journal, cle_chaine, lignes, sceaux, statut, rang_rupture, motif, attendu, trouve, declenchee_par)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """);
        cmd.Parameters.AddWithValue(v.Journal);
        cmd.Parameters.Add(Texte(v.CleChaine));
        cmd.Parameters.AddWithValue(v.Lignes);
        cmd.Parameters.AddWithValue(v.Sceaux);
        cmd.Parameters.AddWithValue(v.Statut);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v.RangRupture ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(Texte(v.Motif));
        cmd.Parameters.Add(Texte(v.Attendu));
        cmd.Parameters.Add(Texte(v.Trouve));
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)v.DeclencheePar ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<DerniereVerification>> DernieresVerificationsAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT ON (journal, cle_chaine) journal, cle_chaine, verifie_le, lignes, sceaux, statut, rang_rupture, motif, attendu, trouve,
                   declenchee_par
            FROM verification_integrite ORDER BY journal, cle_chaine, verifie_le DESC, id DESC
            """);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<DerniereVerification>();
        while (await r.ReadAsync(ct))
            liste.Add(new DerniereVerification(r.GetString(0), Texte(r, 1), DateTime.SpecifyKind(r.GetDateTime(2), DateTimeKind.Utc), r.GetInt64(3),
                r.GetInt32(4), r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), Texte(r, 7), Texte(r, 8), Texte(r, 9),
                r.IsDBNull(10) ? null : r.GetInt64(10)));
        return liste;
    }

    public async Task<ParametresIntegrite> ParametresAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT algorithme_defaut, heure_verification, format_export_sceau FROM parametre_integrite WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new ParametresIntegrite(r.GetString(0), r.GetInt16(1), r.GetString(2)) : new ParametresIntegrite("sha256", 3, "hex");
    }

    public async Task JournaliserRuptureSystemeAsync(string journal, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO journal_audit (utilisateur_id, action, table_cible, enregistrement_id, details) VALUES (NULL, 'integrite_rupture', $1, $2, $3)
            """);
        cmd.Parameters.AddWithValue(journal);
        cmd.Parameters.Add(new NpgsqlParameter { Value = details.TryGetValue("rang", out var rang) && rang is long l ? l : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = JsonSerializer.Serialize(details, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Nom de table pris dans le registre (jamais du texte libre) et entouré de guillemets.</summary>
    private async Task<string> TableAsync(string journal, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("SELECT quote_ident(journal) FROM chaine_integrite WHERE journal = $1");
        cmd.Parameters.AddWithValue(journal);
        return await cmd.ExecuteScalarAsync(ct) as string ?? throw new InvalidOperationException($"Journal {journal} non chaîné.");
    }

    private static string? Texte(NpgsqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static NpgsqlParameter Texte(string? v) => new() { Value = (object?)v ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };
}
