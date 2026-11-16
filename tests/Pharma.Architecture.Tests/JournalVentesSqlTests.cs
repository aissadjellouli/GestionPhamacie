using System.Text.Json;
using Npgsql;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Journal fiscal des ventes (lot 3, script 112) sur la base de test : transactions toujours annulées, rien n'est conservé.
/// Les lignes du journal sont écrites par des déclencheurs différés : « SET CONSTRAINTS ALL IMMEDIATE » les déclenche avant l'annulation.
/// Sans base joignable (ou sans le script 112), les tests ne font rien.
/// </summary>
public class JournalVentesSqlTests
{
    [Fact]
    public async Task Vente_encaissee_puis_annulee_ecrit_deux_lignes_chainees_et_le_ticket_garde_son_numero()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        var (venteId, numero) = await VenteAsync(cx, tx, 119m, 19m);
        await ExecAsync(cx, tx, "UPDATE vente SET statut = 'validee' WHERE id = $1", venteId);
        await ExecAsync(cx, tx, "SET CONSTRAINTS ALL IMMEDIATE");
        await ExecAsync(cx, tx, "UPDATE vente SET statut = 'annulee' WHERE id = $1", venteId);
        await ExecAsync(cx, tx, "SET CONSTRAINTS ALL IMMEDIATE");

        var lignes = await LignesAsync(cx, tx, venteId);
        Assert.Equal(["vente", "annulation"], lignes.Select(l => l.Evenement));
        Assert.All(lignes, l => Assert.Equal(numero, l.Ticket));
        Assert.Equal((119m, 100m, 19m), (lignes[0].Ttc, lignes[0].Ht, lignes[0].Tva));
        Assert.Equal((-119m, -100m, -19m), (lignes[1].Ttc, lignes[1].Ht, lignes[1].Tva));
        Assert.Equal("19.00:119.00:100.00:19.00", lignes[0].Detail);
        Assert.Equal(lignes[0].Rang + 1, lignes[1].Rang);
        // Chaîne intacte, vente toujours au même numéro, journal non modifiable
        Assert.True(await ScalaireAsync<bool>(cx, tx, "SELECT bool_and(rang_rupture IS NULL) FROM integrite_verifier('journal_vente')"));
        Assert.Equal(numero, await ScalaireAsync<string>(cx, tx, "SELECT numero FROM vente WHERE id = $1", venteId));
        await ExecAsync(cx, tx, "SAVEPOINT s");
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cx, tx, "UPDATE journal_vente SET montant_ttc = 0 WHERE vente_id = $1", venteId));
        await ExecAsync(cx, tx, "ROLLBACK TO SAVEPOINT s");
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Vente_restee_en_cours_n_ecrit_rien_et_ligne_impossible_annule_la_vente()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        var (venteId, _) = await VenteAsync(cx, tx, 50m, 0m);
        await ExecAsync(cx, tx, "SET CONSTRAINTS ALL IMMEDIATE");
        Assert.Empty(await LignesAsync(cx, tx, venteId));
        // Récapitulatif TVA : médicament exonéré (taux 0) après encaissement
        await ExecAsync(cx, tx, "UPDATE vente SET statut = 'validee' WHERE id = $1", venteId);
        await ExecAsync(cx, tx, "SET CONSTRAINTS ALL IMMEDIATE");
        Assert.Equal("0.00:50.00:50.00:0.00", Assert.Single(await LignesAsync(cx, tx, venteId)).Detail);
        Assert.True(await ScalaireAsync<bool>(cx, tx,
            "SELECT EXISTS (SELECT 1 FROM journal_vente_recap_tva(current_date - 1, current_date + 1) WHERE taux = 0 AND montant_ttc >= 50)"));
        await tx.RollbackAsync();

        // Écriture de la ligne impossible (isolation refusée par la brique) : la validation de la transaction échoue, la vente n'existe pas
        await using var tx2 = await cx.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        var (autre, _) = await VenteAsync(cx, tx2, 10m, 0m);
        await ExecAsync(cx, tx2, "UPDATE vente SET statut = 'validee' WHERE id = $1", autre);
        await Assert.ThrowsAsync<PostgresException>(() => tx2.CommitAsync());
        Assert.Equal(0L, await ScalaireAsync<long>(cx, null, "SELECT count(*) FROM vente WHERE id = $1", autre));
    }

    [Fact]
    public async Task Numerotation_sans_trou_sous_concurrence_le_numero_d_une_transaction_annulee_est_repris()
    {
        await using var a = await OuvrirAsync();
        await using var b = await OuvrirAsync();
        if (a is null || b is null) return;
        await using var ta = await a.BeginTransactionAsync();
        var premier = await ScalaireAsync<string>(a, ta, "SELECT prochain_numero_vente()");
        // La seconde caisse attend la fin de la première (ligne du compteur verrouillée)
        await using var tb = await b.BeginTransactionAsync();
        var attente = ScalaireAsync<string>(b, tb, "SELECT prochain_numero_vente()");
        await Task.Delay(300);
        Assert.False(attente.IsCompleted);
        await ta.RollbackAsync();
        Assert.Equal(premier, await attente);
        await tb.RollbackAsync();
    }

    [Fact]
    public async Task Compteur_et_cloture_fiscale_ne_se_modifient_pas()
    {
        await using var cx = await OuvrirAsync();
        if (cx is null) return;
        await using var tx = await cx.BeginTransactionAsync();
        await ExecAsync(cx, tx, "SAVEPOINT s");
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cx, tx, "UPDATE compteur_ticket SET dernier_numero = dernier_numero - 1"));
        await ExecAsync(cx, tx, "ROLLBACK TO SAVEPOINT s");
        await Assert.ThrowsAsync<PostgresException>(() => ExecAsync(cx, tx, "DELETE FROM journal_vente"));
        await tx.RollbackAsync();
    }

    // ---- Outils ----

    private static async Task<(long Id, string Numero)> VenteAsync(NpgsqlConnection cx, NpgsqlTransaction tx, decimal montant, decimal taux)
    {
        var numero = await ScalaireAsync<string>(cx, tx, "SELECT prochain_numero_vente()");
        var id = await ScalaireAsync<long>(cx, tx, """
            INSERT INTO vente (numero, utilisateur_id, type, montant_total, part_organisme, part_patient, statut)
            VALUES ($1, (SELECT min(id) FROM utilisateur), 'libre', $2, 0, $2, 'en_cours') RETURNING id
            """, numero, montant);
        await ExecAsync(cx, tx, """
            INSERT INTO ligne_vente (vente_id, produit_id, lot_id, quantite, prix_unitaire, montant_organisme, montant_patient)
            SELECT $1, l.produit_id, l.id, 1, $2, 0, $2 FROM lot l JOIN produit p ON p.id = l.produit_id WHERE p.taux_tva = $3
              AND l.date_peremption > current_date + 60 AND NOT l.bloque AND NOT p.bloque_vente AND p.actif ORDER BY l.id LIMIT 1
            """, id, montant, taux);
        return (id, numero);
    }

    private sealed record Ligne(long Rang, string Evenement, string? Ticket, decimal Ttc, decimal Ht, decimal Tva, string Detail);

    private static async Task<List<Ligne>> LignesAsync(NpgsqlConnection cx, NpgsqlTransaction tx, long venteId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT rang, evenement, numero_ticket, montant_ttc, montant_ht, montant_tva, detail_tva FROM journal_vente WHERE vente_id = $1 ORDER BY rang", cx, tx);
        cmd.Parameters.AddWithValue(venteId);
        await using var r = await cmd.ExecuteReaderAsync();
        var liste = new List<Ligne>();
        while (await r.ReadAsync())
            liste.Add(new Ligne(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetDecimal(3), r.GetDecimal(4), r.GetDecimal(5), r.GetString(6)));
        return liste;
    }

    private static async Task<T> ScalaireAsync<T>(NpgsqlConnection cx, NpgsqlTransaction? tx, string sql, params object[] valeurs)
    {
        await using var cmd = new NpgsqlCommand(sql, cx, tx);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        return (T)(await cmd.ExecuteScalarAsync())!;
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
            await using var cmd = new NpgsqlCommand("SELECT to_regclass('journal_vente') IS NOT NULL AND EXISTS (SELECT 1 FROM lot)", cx);
            if ((bool)(await cmd.ExecuteScalarAsync())!) return cx;
            await cx.DisposeAsync();
            return null;
        }
        catch (NpgsqlException)
        {
            return null;
        }
    }

    private static string ChaineConnexion()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_TEST_CNX") is { Length: > 0 } cnx) return cnx;
        var appsettings = Path.Combine(RacineDepot(), "src", "Pharma.Api", "appsettings.json");
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
