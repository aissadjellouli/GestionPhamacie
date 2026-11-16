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
    private readonly CancellationTokenSource arret = new();
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

    /// <summary>
    /// Écriture ligne par ligne : une erreur (disque plein ou débranché, droits, fichier verrouillé) ne fait perdre que la ligne en cours.
    /// Le fichier est fermé puis rouvert après une courte attente (1 s, puis jusqu'à 30 s si l'erreur dure) ; l'échec est signalé
    /// sur la sortie d'erreur, et le nombre de lignes perdues est écrit dans le fichier dès que l'écriture reprend.
    /// Le journal ne doit jamais arrêter le serveur.
    /// </summary>
    private async Task EcrireAsync()
    {
        StreamWriter? fichier = null;
        DateOnly? jour = null;
        var echecsSuivis = 0;
        var lignesPerdues = 0;
        string? derniereErreur = null;
        try
        {
            await foreach (var ligne in file.Reader.ReadAllAsync())
            {
                // Deux essais par ligne : le second avec un fichier rouvert
                for (var essai = 1; essai <= 2; essai++)
                {
                    try
                    {
                        var aujourdhui = DateOnly.FromDateTime(DateTime.Now);
                        if (fichier is null || jour != aujourdhui || fichier.BaseStream.Length >= tailleMax)
                        {
                            if (jour != aujourdhui) Purger(options.Dossier, options.RetentionMois, DateTime.Now);
                            Fermer(fichier);
                            fichier = null;
                            fichier = Ouvrir(aujourdhui);
                            jour = aujourdhui;
                        }
                        if (lignesPerdues > 0)
                        {
                            await fichier.WriteLineAsync(LignePerdues(lignesPerdues, derniereErreur));
                            lignesPerdues = 0;
                        }
                        await fichier.WriteLineAsync(ligne);
                        if (file.Reader.Count == 0) await fichier.FlushAsync();
                        if (echecsSuivis > 0) Signaler("Journal fichier : écriture rétablie.");
                        echecsSuivis = 0;
                        break;
                    }
                    catch (Exception e)
                    {
                        // Fichier fermé (le flux peut être inutilisable) puis rouvert au prochain essai
                        Fermer(fichier);
                        fichier = null;
                        jour = null;
                        derniereErreur = e.Message;
                        if (echecsSuivis++ == 0) Signaler($"Journal fichier : échec d'écriture dans {options.Dossier} ({e.GetType().Name} : {e.Message}). Nouvel essai.");
                        if (essai == 2) lignesPerdues++;
                        await AttendreAsync(echecsSuivis);
                    }
                }
            }
        }
        catch (Exception e) { Signaler($"Journal fichier arrêté : {e}"); /* ne doit jamais arrêter le serveur */ }
        finally { Fermer(fichier); }
    }

    private static string LignePerdues(int nombre, string? erreur) =>
        new StringBuilder()
            .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append(" [AVT] Pharma.Api.Journalisation : ").Append(nombre).Append(" ligne(s) du journal perdue(s) : échec d'écriture")
            .Append(erreur is null ? "" : $" ({erreur})").ToString();

    /// <summary>Attente avant un nouvel essai : 1 s, 2 s, 4 s… jusqu'à 30 s ; écourtée à l'arrêt du serveur.</summary>
    private async Task AttendreAsync(int echecs)
    {
        if (arret.IsCancellationRequested) return;
        try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(echecs, 6) - 1))), arret.Token); }
        catch (OperationCanceledException) { }
    }

    private static void Fermer(StreamWriter? fichier)
    {
        if (fichier is null) return;
        try { fichier.Dispose(); }
        catch (Exception) { /* flux déjà inutilisable (disque plein, débranché) : rien à sauver */ }
    }

    /// <summary>Trace d'un échec du journal fichier, hors du fichier : sortie d'erreur (console de l'API lancée à la main).</summary>
    private static void Signaler(string message)
    {
        try { Console.Error.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}"); }
        catch (Exception) { }
    }

    /// <summary>Fichier du jour qui n'a pas atteint la taille maximale : pharma-api-AAAAMMJJ.log, puis _2, _3…</summary>
    private StreamWriter Ouvrir(DateOnly jour)
    {
        Directory.CreateDirectory(options.Dossier);   // dossier supprimé ou disque rebranché
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
        arret.Cancel();   // plus d'attente entre deux essais : les dernières lignes sont écrites tout de suite
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
                .Append(" [").Append(Abrege(niveau)).Append("] ").Append(categorie).Append(" : ").Append(Pharma.Shared.MasqueurJournal.Masquer(formateur(etat, exception)));
            // Filet de sécurité : données personnelles reconnaissables masquées dans le message et la pile d'exception
            if (exception is not null) texte.AppendLine().Append(Pharma.Shared.MasqueurJournal.Masquer(exception));
            fournisseur.Ajouter(texte.ToString());
        }

        private static string Abrege(LogLevel n) => n switch
        {
            LogLevel.Trace => "TRC", LogLevel.Debug => "DBG", LogLevel.Information => "INF",
            LogLevel.Warning => "AVT", LogLevel.Error => "ERR", _ => "CRI"
        };
    }
}
