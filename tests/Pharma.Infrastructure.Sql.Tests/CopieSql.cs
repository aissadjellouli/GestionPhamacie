using System.Diagnostics;
using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Fixture de classe : une copie (CREATE DATABASE ... TEMPLATE modèle) par classe de test, supprimée à la fin. Sans PostgreSQL et sans
/// PHARMA_EXIGER_BASE, elle ne fait rien (les tests sont alors ignorés par [FactSql]) ; avec PHARMA_EXIGER_BASE=1 elle lève la raison.
/// </summary>
public sealed class CopieSql : IAsyncLifetime
{
    public const string ApplicationSut = "sql-test-sut";

    public string Nom { get; private set; } = "";
    public TimeSpan DureeCopie { get; private set; }
    public TimeSpan DureeModele { get; private set; }

    /// <summary>Source des classes testées (application_name sql-test-sut, lock_timeout 10 s, statement_timeout 15 s).</summary>
    public NpgsqlDataSource Source { get; private set; } = null!;

    /// <summary>Source pour préparer et relire les données (même base, sans délais imposés).</summary>
    public NpgsqlDataSource Verif { get; private set; } = null!;

    public Donnees Donnees { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        if (FactSqlAttribute.Indisponibilite() is not null && !OutilsPostgres.ExigerBase) return;
        var modele = BaseModeleSql.Obtenir();
        DureeModele = modele.Duree;
        var nom = NomsJetables.Nouveau(modele.SuiteUtc, "copie");
        var chrono = Stopwatch.StartNew();
        await using (var cx = new NpgsqlConnection(OutilsPostgres.ChaineAdministration()))
        {
            await cx.OpenAsync();
            await using var creer = new NpgsqlCommand($"CREATE DATABASE \"{nom}\" ENCODING 'UTF8' TEMPLATE \"{NomsJetables.Verifier(modele.Nom)}\"", cx);
            await creer.ExecuteNonQueryAsync();
        }
        chrono.Stop();
        DureeCopie = chrono.Elapsed;
        Nom = nom;
        var baseCnx = new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion()) { Database = nom };
        Source = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseCnx.ConnectionString)
        {
            ApplicationName = ApplicationSut, CommandTimeout = 15, Options = "-c lock_timeout=10000 -c statement_timeout=15000"
        }.ConnectionString);
        Verif = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseCnx.ConnectionString) { ApplicationName = "sql-test-verif" }.ConnectionString);
        Donnees = new Donnees(Verif);
    }

    public async Task DisposeAsync()
    {
        if (Nom.Length == 0) return;
        if (Source is not null) await Source.DisposeAsync();
        if (Verif is not null) await Verif.DisposeAsync();
        BaseModeleSql.Supprimer(Nom);
    }
}
