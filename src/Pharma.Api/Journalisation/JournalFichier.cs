using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Threading.Channels;

namespace Pharma.Api.Journalisation;

/// <summary>
/// Réglages du journal du serveur (section « Journaux » : appsettings.json ou variables Journaux__Dossier, Journaux__TailleMaxMo,
/// Journaux__RetentionMois, Journaux__Niveau).
/// </summary>
public sealed class OptionsJournalFichier
{
    public string Dossier { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "Journaux");
    /// <summary>Taille maximale d'un fichier ; au-delà, un nouveau fichier est ouvert le même jour (_2, _3…).</summary>
    public double TailleMaxMo { get; set; } = 50;
    /// <summary>Les fichiers plus anciens sont supprimés (au démarrage, puis chaque jour).</summary>
    public int RetentionMois { get; set; } = 3;
    public LogLevel Niveau { get; set; } = LogLevel.Information;
}

/// <summary>
/// Journal du serveur en fichiers texte UTF-8 : un fichier par jour (pharma-api-AAAAMMJJ.log), découpé à la taille maximale,
/// conservé le nombre de mois réglé. Écrit par une tâche de fond : une requête n'attend jamais le disque.
/// </summary>
public sealed class JournalFichierProvider : ILoggerProvider
{
    public const string Prefixe = "pharma-api-";
    private readonly OptionsJournalFichier options;
    private readonly long tailleMax;
    private readonly Channel<string> file = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly ConcurrentDictionary<string, Journal> journaux = new();
    private readonly Task ecriture;

    public JournalFichierProvider(OptionsJournalFichier options)
    {
        this.options = options;
        tailleMax = (long)(Math.Max(options.TailleMaxMo, 0.001) * 1024 * 1024);
        Directory.CreateDirectory(options.Dossier);
        ecriture = Task.Run(EcrireAsync);
    }

    public ILogger CreateLogger(string categorie) => journaux.GetOrAdd(categorie, c => new Journal(this, c));

    private void Ajouter(string ligne) => file.Writer.TryWrite(ligne);

    private async Task EcrireAsync()
    {
        StreamWriter? fichier = null;
        DateOnly? jour = null;
        try
        {
            await foreach (var ligne in file.Reader.ReadAllAsync())
            {
                var aujourdhui = DateOnly.FromDateTime(DateTime.Now);
                if (fichier is null || jour != aujourdhui || fichier.BaseStream.Length >= tailleMax)
                {
                    if (jour != aujourdhui) Purger(options.Dossier, options.RetentionMois, DateTime.Now);
                    fichier?.Dispose();
                    fichier = Ouvrir(aujourdhui);
                    jour = aujourdhui;
                }
                await fichier.WriteLineAsync(ligne);
                if (file.Reader.Count == 0) await fichier.FlushAsync();
            }
        }
        catch (Exception) { /* le journal ne doit jamais arrêter le serveur (disque plein, droits) */ }
        finally { fichier?.Dispose(); }
    }

    /// <summary>Fichier du jour qui n'a pas atteint la taille maximale : pharma-api-AAAAMMJJ.log, puis _2, _3…</summary>
    private StreamWriter Ouvrir(DateOnly jour)
    {
        for (var n = 1; ; n++)
        {
            var chemin = Path.Combine(options.Dossier, NomFichier(jour, n));
            if (File.Exists(chemin) && new FileInfo(chemin).Length >= tailleMax) continue;
            var flux = new FileStream(chemin, FileMode.Append, FileAccess.Write, FileShare.Read);
            return new StreamWriter(flux, new UTF8Encoding(false));
        }
    }

    internal static string NomFichier(DateOnly jour, int numero) =>
        $"{Prefixe}{jour:yyyyMMdd}{(numero > 1 ? $"_{numero}" : "")}.log";

    /// <summary>Supprime les fichiers du journal modifiés pour la dernière fois il y a plus de « mois » mois.</summary>
    internal static int Purger(string dossier, int mois, DateTime maintenant)
    {
        if (mois <= 0 || !Directory.Exists(dossier)) return 0;
        var limite = maintenant.AddMonths(-mois);
        var supprimes = 0;
        foreach (var f in Directory.EnumerateFiles(dossier, $"{Prefixe}*.log"))
            try
            {
                if (File.GetLastWriteTime(f) < limite) { File.Delete(f); supprimes++; }
            }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        return supprimes;
    }

    public void Dispose()
    {
        file.Writer.TryComplete();
        ecriture.Wait(TimeSpan.FromSeconds(5));
    }

    private sealed class Journal(JournalFichierProvider fournisseur, string categorie) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel niveau) => niveau != LogLevel.None && niveau >= fournisseur.options.Niveau
            // Le détail des requêtes ASP.NET reste dans la console : au fichier, seulement ses avertissements et erreurs
            && (niveau >= LogLevel.Warning || !categorie.StartsWith("Microsoft.", StringComparison.Ordinal));

        public void Log<TState>(LogLevel niveau, EventId id, TState etat, Exception? exception, Func<TState, Exception?, string> formateur)
        {
            if (!IsEnabled(niveau)) return;
            var texte = new StringBuilder()
                .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
                .Append(" [").Append(Abrege(niveau)).Append("] ").Append(categorie).Append(" : ").Append(formateur(etat, exception));
            if (exception is not null) texte.AppendLine().Append(exception);
            fournisseur.Ajouter(texte.ToString());
        }

        private static string Abrege(LogLevel n) => n switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "AVT", LogLevel.Error => "ERR", _ => "CRI"
        };
    }
}
