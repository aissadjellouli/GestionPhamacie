using System.Text.Json;
using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Registre des psychotropes chaîné (lot 8, script 117) sur la base de test, en transaction toujours annulée : une ligne ajoutée est
/// recopiée dans le journal chaîné, registre et journal concordent, une modification simulée est détectée. Sans base joignable ou
/// sans script 117, les tests ne font rien.
/// </summary>
public class RegistreChaineTests
{
    [Fact]
    public async Task Ligne_ajoutee_recopiee_et_modification_detectee()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        var reference = "XUNIT-117-" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(cx, tx, """
            INSERT INTO registre_psychotrope (lot_id, type_mouvement, quantite, solde_apres, utilisateur_id, reference)
            SELECT (SELECT min(id) FROM lot), 'entree', 1, 1, (SELECT min(id) FROM utilisateur), $1
            """, reference);
        Assert.Equal(1L, await ScalaireAsync<long>(cx, tx, """
            SELECT count(*) FROM journal_registre_psychotrope j JOIN registre_psychotrope r ON r.id = j.registre_id
            WHERE j.nature = 'inscription' AND r.reference = $1
            """, reference));
        Assert.Null(await ScalaireAsync<string?>(cx, tx, "SELECT motif FROM registre_concordance()"));
        Assert.Null(await ScalaireAsync<long?>(cx, tx, "SELECT rang_rupture FROM integrite_verifier('journal_registre_psychotrope', 'officine')"));

        await ExecAsync(cx, tx, "ALTER TABLE registre_psychotrope DISABLE TRIGGER trg_registre_immuable");
        await ExecAsync(cx, tx, "UPDATE registre_psychotrope SET quantite = 2 WHERE reference = $1", reference);
        Assert.StartsWith("ligne du registre modifiée", await ScalaireAsync<string?>(cx, tx, "SELECT motif FROM registre_concordance()"));
        await tx.RollbackAsync();
    }

    private static async Task<T> ScalaireAsync<T>(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        var r = await cmd.ExecuteScalarAsync();
        return r is DBNull or null ? default! : (T)r;
    }

    private static async Task ExecAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string sql, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection?> OuvrirAsync()
    {
        try
        {
            var cx = new NpgsqlConnection(ChaineConnexion());
            await cx.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT to_regclass('journal_registre_psychotrope') IS NOT NULL AND EXISTS (SELECT 1 FROM lot)", cx);
            if ((bool)(await cmd.ExecuteScalarAsync())!) return cx;
            await cx.DisposeAsync();
            return null;   // script 117 non passé, ou base sans lot
        }
        catch (NpgsqlException)
        {
            return null;   // pas de base de test joignable
        }
    }

    private static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var racine = Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } r ? r : Racine();
        // Le mot de passe de développement est dans appsettings.Development.json (ignoré par git), sinon appsettings.json
        var dossierApi = Path.Combine(racine, "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }

    private static string Racine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx"))) dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
