using System.Text;
using System.Text.Json;
using Npgsql;
using Pharma.Application.Integrite;
using Pharma.Infrastructure.Securite;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Intégrité des journaux (lot 2) : clé de scellement locale (DPAPI) et brique SQL du script 111 sur la base de test
/// (table d'essai integrite_essai, transactions toujours annulées : rien n'est conservé). Sans base joignable, les tests SQL ne font rien.
/// Base : variable PHARMA_TEST_CNX, sinon la chaîne « Pharmacie » de src/Pharma.Api/appsettings.json (pharmacie_test).
/// </summary>
public class IntegriteJournauxTests
{
    // ---- Clé de scellement ----

    [Fact]
    public void Cle_absente_creee_au_premier_usage_puis_relue_a_l_identique()
    {
        if (!OperatingSystem.IsWindows()) return;
        var dossier = Path.Combine(Path.GetTempPath(), "pharma-integrite-" + Guid.NewGuid().ToString("N"));
        var chemin = Path.Combine(dossier, "cle-integrite.dat");
        try
        {
            var message = "sceau-v1|integrite_essai|officine|manuel"u8.ToArray();
            var premier = new ScelleurIntegriteLocal(chemin);
            Assert.False(File.Exists(chemin));
            var sceau = premier.Sceller(message);
            Assert.True(File.Exists(chemin));
            Assert.Equal(32, sceau.Length);
            Assert.Equal("HMAC-SHA256", premier.Algorithme);

            // Le fichier n'est pas la clé en clair, et la clé relue (autre instance) donne le même sceau
            var relu = new ScelleurIntegriteLocal(chemin);
            Assert.Equal(premier.EmpreinteCle, relu.EmpreinteCle);
            Assert.True(relu.Verifier(message, sceau));
            Assert.False(relu.Verifier("sceau-v1|integrite_essai|officine|autre"u8.ToArray(), sceau));
            Assert.True(File.ReadAllBytes(chemin).Length > 32);

            // Une autre clé ne valide pas le sceau
            var autre = new ScelleurIntegriteLocal(Path.Combine(dossier, "autre.dat"));
            Assert.NotEqual(premier.EmpreinteCle, autre.EmpreinteCle);
            Assert.False(autre.Verifier(message, sceau));
        }
        finally
        {
            if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
        }
    }

    [Fact]
    public void Cle_illisible_n_est_jamais_remplacee_en_silence()
    {
        if (!OperatingSystem.IsWindows()) return;
        var chemin = Path.Combine(Path.GetTempPath(), "pharma-integrite-" + Guid.NewGuid().ToString("N") + ".dat");
        try
        {
            File.WriteAllBytes(chemin, Encoding.UTF8.GetBytes("pas une clé DPAPI"));
            Assert.Throws<InvalidOperationException>(() => new ScelleurIntegriteLocal(chemin).Sceller([1]));
            Assert.Equal("pas une clé DPAPI", File.ReadAllText(chemin));
        }
        finally
        {
            File.Delete(chemin);
        }
    }

    // ---- Brique SQL (script 111) ----

    [Fact]
    public async Task Verification_detecte_une_modification_et_une_suppression_simulees()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        var cle = "essai-xunit-" + Guid.NewGuid().ToString("N")[..8];
        await ExecAsync(cx, tx, "INSERT INTO integrite_essai (libelle, montant, cle_chaine) VALUES ('a', 1, $1), ('b', 2, $1), ('c', 3, $1), ('d', 4, $1)", cle);
        Assert.Null((await VerifierAsync(cx, tx, cle)).Rang);

        await ExecAsync(cx, tx, "SAVEPOINT s");
        await ExecAsync(cx, tx, "ALTER TABLE integrite_essai DISABLE TRIGGER trg_integrite_immuable");
        await ExecAsync(cx, tx, "UPDATE integrite_essai SET libelle = 'falsifié' WHERE cle_chaine = $1 AND rang = 3", cle);
        var modif = await VerifierAsync(cx, tx, cle);
        Assert.Equal(3L, modif.Rang);
        Assert.StartsWith("ligne modifiée", modif.Motif);
        await ExecAsync(cx, tx, "ROLLBACK TO SAVEPOINT s");

        await ExecAsync(cx, tx, "ALTER TABLE integrite_essai DISABLE TRIGGER trg_integrite_immuable");
        await ExecAsync(cx, tx, "DELETE FROM integrite_essai WHERE cle_chaine = $1 AND rang = 2", cle);
        var supp = await VerifierAsync(cx, tx, cle);
        Assert.Equal(2L, supp.Rang);
        Assert.StartsWith("ligne manquante", supp.Motif);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Modification_et_suppression_refusees_par_la_base()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        await ExecAsync(cx, tx, "INSERT INTO integrite_essai (libelle, cle_chaine) VALUES ('a', 'essai-refus')");
        await ExecAsync(cx, tx, "SAVEPOINT s");
        var e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cx, tx, "UPDATE integrite_essai SET libelle = 'x' WHERE cle_chaine = 'essai-refus'"));
        Assert.Contains("ni modifié", e.MessageText);
        await ExecAsync(cx, tx, "ROLLBACK TO SAVEPOINT s");
        e = await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cx, tx, "DELETE FROM integrite_essai WHERE cle_chaine = 'essai-refus'"));
        Assert.Contains("ni supprimé", e.MessageText);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Deux_insertions_concurrentes_sont_serialisees_et_restent_chainees()
    {
        await using var a = await OuvrirAsync();
        if (a is null) return;
        await using var b = (await OuvrirAsync())!;
        var cle = "essai-concurrence-" + Guid.NewGuid().ToString("N")[..8];

        // A écrit et garde sa transaction ouverte : elle tient le verrou de la chaîne
        await using var txA = await a.BeginTransactionAsync();
        await ExecAsync(a, txA, "INSERT INTO integrite_essai (libelle, cle_chaine) VALUES ('caisse A', $1)", cle);

        // B (autre caisse) attend le verrou au lieu de prendre le même rang
        await using var txB = await b.BeginTransactionAsync();
        var insertionB = ExecAsync(b, txB, "INSERT INTO integrite_essai (libelle, cle_chaine) VALUES ('caisse B', $1)", cle);
        await Task.Delay(500);
        Assert.False(insertionB.IsCompleted, "La seconde insertion aurait dû attendre le verrou de la chaîne.");

        // A annule : B reprend la suite de la chaîne (rang 1, origine), sans trou ni doublon
        await txA.RollbackAsync();
        await insertionB.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var cmd = new NpgsqlCommand("SELECT rang, hash_precedent = integrite_origine('sha256') FROM integrite_essai WHERE cle_chaine = $1", b, txB))
        {
            cmd.Parameters.AddWithValue(cle);
            await using var r = await cmd.ExecuteReaderAsync();
            Assert.True(await r.ReadAsync());
            Assert.Equal(1L, r.GetInt64(0));
            Assert.True(r.GetBoolean(1));
        }
        // Puis une deuxième ligne de B se chaîne à la première, et la chaîne est intacte
        await ExecAsync(b, txB, "INSERT INTO integrite_essai (libelle, cle_chaine) VALUES ('caisse B 2', $1)", cle);
        var v = await VerifierAsync(b, txB, cle);
        Assert.Null(v.Rang);
        Assert.Equal(2L, v.Lignes);
        await txB.RollbackAsync();
    }

    private static async Task<(long? Rang, string? Motif, long Lignes)> VerifierAsync(NpgsqlConnection cx, NpgsqlTransaction tx, string cle)
    {
        await using var cmd = new NpgsqlCommand("SELECT rang_rupture, motif, lignes FROM integrite_verifier('integrite_essai', $1)", cx, tx);
        cmd.Parameters.AddWithValue(cle);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.IsDBNull(0) ? null : r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2));
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
            await using var cmd = new NpgsqlCommand("SELECT to_regclass('integrite_essai') IS NOT NULL", cx);
            if ((bool)(await cmd.ExecuteScalarAsync())!) return cx;
            await cx.DisposeAsync();
            return null;   // script 111 non passé sur cette base
        }
        catch (NpgsqlException)
        {
            return null;   // pas de base de test joignable
        }
    }

    private static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        // Le mot de passe de développement est dans appsettings.Development.json (ignoré par git), sinon appsettings.json
        var dossierApi = Path.Combine(RacineDepot(), "src", "Pharma.Api");
        var appsettings = Path.Combine(dossierApi, "appsettings.Development.json");
        if (!File.Exists(appsettings)) appsettings = Path.Combine(dossierApi, "appsettings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(appsettings), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        return doc.RootElement.GetProperty("ConnectionStrings").GetProperty("Pharmacie").GetString()!;
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
