using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Seconde connexion qui tient un verrou réel dans une transaction ouverte, pour forcer l'ordre d'un scénario à deux connexions simultanées.
/// On constate l'attente de la méthode testée dans pg_locks (jamais par un délai fixe) avant de modifier la donnée puis de valider.
/// </summary>
public sealed class Bloqueur : IAsyncDisposable
{
    public const string ApplicationBloqueur = "sql-test-bloqueur";

    private readonly NpgsqlConnection cnx;
    private readonly NpgsqlDataSource verif;
    private readonly string baseNom;
    private bool termine;

    private Bloqueur(NpgsqlConnection cnx, NpgsqlDataSource verif, string baseNom)
    {
        this.cnx = cnx;
        this.verif = verif;
        this.baseNom = baseNom;
    }

    /// <summary>Ouvre la connexion du bloqueur et sa transaction (BEGIN).</summary>
    public static async Task<Bloqueur> DemarrerAsync(CopieSql copie)
    {
        var cs = new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion())
        {
            Database = NomsJetables.Verifier(copie.Nom), ApplicationName = ApplicationBloqueur, Pooling = false,
            Options = "-c lock_timeout=10000 -c statement_timeout=15000"
        }.ConnectionString;
        var cx = new NpgsqlConnection(cs);
        await cx.OpenAsync();
        var b = new Bloqueur(cx, copie.Verif, copie.Nom);
        await b.ExecuterAsync("BEGIN");
        return b;
    }

    /// <summary>Exécute une commande dans la transaction du bloqueur (SELECT ... FOR UPDATE / FOR SHARE, UPDATE...). SQL paramétré.</summary>
    public async Task<int> ExecuterAsync(string sql, params (string Nom, object? Valeur)[] parametres)
    {
        await using var cmd = new NpgsqlCommand(sql, cnx);
        foreach (var (nom, valeur) in parametres) cmd.Parameters.AddWithValue(nom, valeur ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>Verrou consultatif de transaction, même clé que le code testé : pg_advisory_xact_lock(hashtextextended(cle, 0)).</summary>
    public Task VerrouConsultatifAsync(string cle) =>
        ExecuterAsync("SELECT pg_advisory_xact_lock(hashtextextended(@cle, 0))", ("cle", cle));

    /// <summary>
    /// Attend que <paramref name="n"/> sessions de la méthode testée (application_name sql-test-sut) soient en attente d'un verrou
    /// (pg_locks.granted = false). Interroge toutes les 20 ms ; échoue explicitement après le délai (5 s par défaut).
    /// </summary>
    public async Task AttendreBloquesAsync(int n, TimeSpan? delai = null)
    {
        var limite = delai ?? TimeSpan.FromSeconds(5);
        using var cts = new CancellationTokenSource(limite);
        var vu = 0;
        try
        {
            while (true)
            {
                await using var cmd = verif.CreateCommand(
                    "SELECT count(DISTINCT l.pid) FROM pg_locks l JOIN pg_stat_activity a ON a.pid = l.pid " +
                    "WHERE NOT l.granted AND a.application_name = @app AND a.datname = @base");
                cmd.Parameters.AddWithValue("app", CopieSql.ApplicationSut);
                cmd.Parameters.AddWithValue("base", baseNom);
                vu = Convert.ToInt32(await cmd.ExecuteScalarAsync(cts.Token));
                if (vu >= n) return;
                await Task.Delay(20, cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            throw new Xunit.Sdk.XunitException($"La méthode n'a pas attendu le verrou : {vu} session(s) en attente sur {n} attendue(s) après {limite.TotalSeconds:0.#} s.");
        }
    }

    public async Task ValiderAsync()
    {
        if (termine) return;
        termine = true;
        await ExecuterAsync("COMMIT");
    }

    public async ValueTask DisposeAsync()
    {
        if (!termine)
        {
            termine = true;
            try { await ExecuterAsync("ROLLBACK"); } catch (NpgsqlException) { /* connexion fermée juste après */ }
        }
        await cnx.DisposeAsync();
    }
}
