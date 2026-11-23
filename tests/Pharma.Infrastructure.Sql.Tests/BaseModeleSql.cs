using System.Diagnostics;
using System.Text;
using Npgsql;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>
/// Base modèle, créée une fois par exécution de la suite depuis database/init (schema.sql + 083 fuseau + seed_referentiel.sql, comme
/// scripts/mep/creer_base_officine.ps1). Aucune connexion .NET n'y reste ouverte (sinon CREATE DATABASE ... TEMPLATE échouerait) ; supprimée à
/// la fin du processus. Au premier appel, purge les bases orphelines pharmacie_jetable_sql_* d'une suite plus ancienne.
/// </summary>
internal static class BaseModeleSql
{
    /// <summary>Marge sous laquelle une base jetable n'est jamais purgée (une autre exécution peut être en cours).</summary>
    private static readonly TimeSpan MargePurge = TimeSpan.FromMinutes(15);

    private static readonly Lazy<Resultat> Instance = new(Creer);

    internal sealed record Resultat(string Nom, TimeSpan Duree, DateTime SuiteUtc, int Purgees);

    public static Resultat Obtenir() => Instance.Value;

    private static Resultat Creer()
    {
        if (FactSqlAttribute.Indisponibilite() is { } raison)
            throw new InvalidOperationException("PostgreSQL indisponible et PHARMA_EXIGER_BASE=1 : " + raison);
        var suite = DateTime.UtcNow;
        var chrono = Stopwatch.StartNew();
        var purgees = PurgerOrphelines(suite);
        var nom = NomsJetables.Nouveau(suite, "modele");
        using (var cx = new NpgsqlConnection(OutilsPostgres.ChaineAdministration()))
        {
            cx.Open();
            using var creer = new NpgsqlCommand($"CREATE DATABASE \"{NomsJetables.Verifier(nom)}\" ENCODING 'UTF8' TEMPLATE template0", cx);
            creer.ExecuteNonQuery();
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Supprimer(nom);
        try
        {
            var racine = OutilsPostgres.RacineDepot();
            var init = Path.Combine(racine, "database", "init");
            var fuseau = Directory.GetFiles(Path.Combine(racine, "database", "migration"), "083_*.sql").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault()
                         ?? throw new FileNotFoundException("Script 083 (fuseau horaire) introuvable dans database/migration.");
            ExecuterFichier(nom, Path.Combine(init, "schema.sql"));
            ExecuterFichier(nom, fuseau);
            ExecuterFichier(nom, Path.Combine(init, "seed_referentiel.sql"));
        }
        catch
        {
            Supprimer(nom);
            throw;
        }
        chrono.Stop();
        return new Resultat(nom, chrono.Elapsed, suite, purgees);
    }

    private static void ExecuterFichier(string nom, string fichier)
    {
        NomsJetables.Verifier(nom);
        var cnx = new NpgsqlConnectionStringBuilder(OutilsPostgres.ChaineConnexion());
        var info = new ProcessStartInfo(OutilsPostgres.Outil("psql.exe")!)
        {
            UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "--no-password", $"--host={cnx.Host}", $"--port={cnx.Port}", $"--username={cnx.Username}", $"--dbname={nom}",
                     "-v", "ON_ERROR_STOP=1", "-q", "-f", fichier.Replace('\\', '/') })
            info.ArgumentList.Add(a);
        info.Environment["PGPASSWORD"] = cnx.Password ?? "";
        info.Environment["PGCLIENTENCODING"] = "UTF8";
        info.Environment["PGOPTIONS"] = "-c client_min_messages=warning";
        using var p = Process.Start(info) ?? throw new InvalidOperationException("psql n'a pas pu démarrer.");
        var sortie = p.StandardOutput.ReadToEndAsync();
        var erreurs = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        _ = sortie.Result;
        if (p.ExitCode != 0)
        {
            var texte = erreurs.Result.Trim();
            throw new InvalidOperationException($"psql a échoué sur {Path.GetFileName(fichier)} : {(texte.Length > 800 ? texte[..800] + "..." : texte)}");
        }
    }

    /// <summary>Supprime les bases du format jetable, plus vieilles que cette suite (de plus de <see cref="MargePurge"/>) et sans session connectée.</summary>
    private static int PurgerOrphelines(DateTime suite)
    {
        var n = 0;
        try
        {
            using var cx = new NpgsqlConnection(OutilsPostgres.ChaineAdministration());
            cx.Open();
            var candidates = new List<string>();
            using (var liste = new NpgsqlCommand(
                       "SELECT d.datname FROM pg_database d WHERE d.datname LIKE 'pharmacie\\_jetable\\_sql\\_%' " +
                       "AND NOT EXISTS (SELECT 1 FROM pg_stat_activity a WHERE a.datname = d.datname)", cx))
            using (var lecteur = liste.ExecuteReader())
                while (lecteur.Read()) candidates.Add(lecteur.GetString(0));
            foreach (var nom in candidates)
            {
                if (NomsJetables.Horodatage(nom) is not { } date || date >= suite - MargePurge) continue;
                using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{NomsJetables.Verifier(nom)}\" WITH (FORCE)", cx);
                supprimer.ExecuteNonQuery();
                n++;
            }
        }
        catch (NpgsqlException) { /* purge au mieux : une base restée sera reprise à la prochaine exécution */ }
        return n;
    }

    internal static void Supprimer(string nom)
    {
        try
        {
            using var cx = new NpgsqlConnection(OutilsPostgres.ChaineAdministration());
            cx.Open();
            using var supprimer = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{NomsJetables.Verifier(nom)}\" WITH (FORCE)", cx);
            supprimer.ExecuteNonQuery();
        }
        catch (NpgsqlException) { /* restée : reprise par la purge de la prochaine exécution */ }
    }
}
