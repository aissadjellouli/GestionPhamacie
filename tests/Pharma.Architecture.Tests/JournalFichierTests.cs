using Microsoft.Extensions.Logging;
using Pharma.Shared.Journalisation;

namespace Pharma.Architecture.Tests;

/// <summary>Journal du serveur en fichiers : une erreur d'écriture ne l'arrête pas, l'écriture reprend quand le fichier redevient accessible.</summary>
public sealed class JournalFichierTests : IDisposable
{
    private readonly string dossier = Path.Combine(Path.GetTempPath(), "journal-fichier-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(dossier, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public async Task DisposeAsync_ecrit_les_dernieres_lignes_sans_bloquer_et_Dispose_reste_sur()
    {
        // Revue du 2026-11-23 : Dispose attendait l'écriture par un Wait(5 s) ; arrêt asynchrone, rejouable
        var provider = new JournalFichierProvider(new OptionsJournalFichier { Dossier = dossier });
        provider.CreateLogger("Essai").LogInformation("dernière ligne avant l'arrêt");
        await provider.DisposeAsync();
        var fichier = Directory.GetFiles(dossier, "*.log").Single();
        using (var lecture = new FileStream(fichier, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Contains("dernière ligne avant l'arrêt", new StreamReader(lecture).ReadToEnd());
        await provider.DisposeAsync();   // deuxième arrêt : sans effet, sans exception
        provider.Dispose();

        // Dispose seul (fabrique de journaux synchrone) : même résultat
        var autre = new JournalFichierProvider(new OptionsJournalFichier { Dossier = dossier, Prefixe = "autre-" });
        autre.CreateLogger("Essai").LogInformation("ligne du Dispose synchrone");
        autre.Dispose();
        autre.Dispose();
        using var lecture2 = new FileStream(Directory.GetFiles(dossier, "autre-*.log").Single(), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Contains("ligne du Dispose synchrone", new StreamReader(lecture2).ReadToEnd());
    }

    [Fact]
    public void Ecriture_reprise_apres_un_fichier_inaccessible_et_lignes_perdues_signalees()
    {
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, $"{JournalFichierProvider.PrefixeApiLocale}{DateTime.Now:yyyyMMdd}.log");
        var provider = new JournalFichierProvider(new OptionsJournalFichier { Dossier = dossier });
        try
        {
            var journal = provider.CreateLogger("Essai");
            // Fichier du jour verrouillé par un autre programme : les deux essais d'écriture échouent (1 s d'attente entre eux)
            using (new FileStream(chemin, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                journal.LogInformation("ligne pendant le verrou");
                Thread.Sleep(1600);
            }
            journal.LogInformation("ligne après le verrou");

            var contenu = "";
            for (var i = 0; i < 150 && !contenu.Contains("ligne après le verrou"); i++)
            {
                Thread.Sleep(100);
                try
                {
                    using var lecture = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    contenu = new StreamReader(lecture).ReadToEnd();
                }
                catch (IOException) { }
            }
            Assert.Contains("ligne après le verrou", contenu);
            Assert.Contains("1 ligne(s) du journal perdue(s)", contenu);
            Assert.DoesNotContain("ligne pendant le verrou", contenu);
        }
        finally { provider.Dispose(); }
    }
}
