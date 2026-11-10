using System.Diagnostics;
using Npgsql;
using NpgsqlTypes;
using Pharma.Application.Sauvegardes;

namespace Pharma.Infrastructure.Sauvegardes;

public sealed class SauvegardesSql(NpgsqlDataSource db) : ISauvegardesDepot
{
    public async Task<ReglagesSauvegarde> ReglagesAsync(CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("SELECT actif, dossier, heure, conservation, derniere_execution FROM parametre_sauvegarde WHERE id = 1");
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct))
            throw new InvalidOperationException("Réglages des sauvegardes absents : exécutez database/014_sauvegardes.sql.");
        return new ReglagesSauvegarde(r.GetBoolean(0), r.IsDBNull(1) ? null : r.GetString(1), TimeOnly.FromTimeSpan(r.GetTimeSpan(2)),
            r.GetInt32(3), r.IsDBNull(4) ? null : r.GetFieldValue<DateOnly>(4));
    }

    public async Task EnregistrerReglagesAsync(bool actif, string? dossier, TimeOnly heure, int conservation, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_sauvegarde SET actif = $1, dossier = $2, heure = $3, conservation = $4 WHERE id = 1");
        cmd.Parameters.AddWithValue(actif);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)dossier ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.AddWithValue(heure.ToTimeSpan());
        cmd.Parameters.AddWithValue(conservation);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task NoterExecutionAsync(DateOnly jour, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("UPDATE parametre_sauvegarde SET derniere_execution = $1 WHERE id = 1");
        cmd.Parameters.AddWithValue(jour);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<long> JournaliserAsync(NouvelleEntreeSauvegarde e, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            INSERT INTO journal_sauvegarde (debut, fin, statut, declenchement, fichier, taille_octets, message, utilisateur_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8) RETURNING id
            """);
        cmd.Parameters.AddWithValue(e.Debut);
        cmd.Parameters.AddWithValue(e.Fin);
        cmd.Parameters.AddWithValue(e.Statut);
        cmd.Parameters.AddWithValue(e.Declenchement);
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)e.Fichier ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)e.TailleOctets ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)e.Message ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        cmd.Parameters.Add(new NpgsqlParameter { Value = (object?)e.UtilisateurId ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint });
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<IReadOnlyList<EntreeSauvegarde>> JournalAsync(int limite, CancellationToken ct = default)
    {
        await using var cmd = db.CreateCommand("""
            SELECT j.id, j.debut, j.fin, j.statut, j.declenchement, j.fichier, j.taille_octets, j.message, u.nom_complet
            FROM journal_sauvegarde j LEFT JOIN utilisateur u ON u.id = j.utilisateur_id
            ORDER BY j.debut DESC LIMIT $1
            """);
        cmd.Parameters.AddWithValue(limite);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        var liste = new List<EntreeSauvegarde>();
        while (await r.ReadAsync(ct))
            liste.Add(new EntreeSauvegarde(r.GetInt64(0), r.GetDateTime(1), r.IsDBNull(2) ? null : r.GetDateTime(2), r.GetString(3), r.GetString(4),
                r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.IsDBNull(7) ? null : r.GetString(7),
                r.IsDBNull(8) ? null : r.GetString(8)));
        return liste;
    }
}

/// <summary>
/// Export par pg_dump (format personnalisé, compressé, restaurable avec pg_restore).
/// pg_dump est cherché dans le réglage Sauvegardes:PgDump, sinon dans C:\Program Files\PostgreSQL\*\bin (version la plus récente).
/// </summary>
public sealed class StockagePgDump(string chaineConnexion, string? cheminPgDump) : IStockageSauvegarde
{
    public bool DisqueDisponible(string dossier)
    {
        var racine = Path.GetPathRoot(dossier);
        if (string.IsNullOrEmpty(racine)) return false;
        try
        {
            if (!new DriveInfo(racine).IsReady && !racine.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            Directory.CreateDirectory(dossier);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    public async Task<long> ExporterBaseAsync(string fichier, CancellationToken ct = default)
    {
        var c = new NpgsqlConnectionStringBuilder(chaineConnexion);
        var pgDump = TrouverPgDump();
        var temporaire = fichier + ".partiel";
        var info = new ProcessStartInfo(pgDump)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true
        };
        foreach (var a in new[] { "--format=custom", "--no-password", $"--host={c.Host}", $"--port={c.Port}",
                                  $"--username={c.Username}", $"--file={temporaire}", c.Database! })
            info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = c.Password ?? "";   // jamais sur la ligne de commande

        using var p = Process.Start(info) ?? throw new InvalidOperationException("pg_dump n'a pas pu démarrer.");
        var erreurs = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
        {
            TryDelete(temporaire);
            throw new InvalidOperationException($"pg_dump a échoué (code {p.ExitCode}) : {(await erreurs).Trim()}");
        }
        File.Move(temporaire, fichier, overwrite: true);   // le fichier n'apparaît qu'une fois complet
        return new FileInfo(fichier).Length;
    }

    public IReadOnlyList<string> Sauvegardes(string dossier) =>
        Directory.Exists(dossier)
            ? new DirectoryInfo(dossier).GetFiles("pharmacie_*.dump").OrderByDescending(f => f.Name).Select(f => f.FullName).ToList()
            : [];

    public void Supprimer(string fichier) => File.Delete(fichier);

    private string TrouverPgDump()
    {
        if (!string.IsNullOrWhiteSpace(cheminPgDump))
            return File.Exists(cheminPgDump) ? cheminPgDump : throw new InvalidOperationException($"pg_dump introuvable : {cheminPgDump}");
        var racine = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        var trouve = Directory.Exists(racine)
            ? Directory.GetDirectories(racine)
                .Select(d => Path.Combine(d, "bin", "pg_dump.exe")).Where(File.Exists)
                .OrderByDescending(f => int.TryParse(Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(f))), out var v) ? v : 0)
                .FirstOrDefault()
            : null;
        return trouve ?? throw new InvalidOperationException("pg_dump introuvable : renseignez Sauvegardes:PgDump dans appsettings.json.");
    }

    private static void TryDelete(string f)
    {
        try { File.Delete(f); } catch (IOException) { }
    }
}
