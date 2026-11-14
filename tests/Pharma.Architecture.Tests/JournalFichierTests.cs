using Microsoft.Extensions.Logging;
using Pharma.Api.Journalisation;

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
    public void Ecriture_reprise_apres_un_fichier_inaccessible_et_lignes_perdues_signalees()
    {
        Directory.CreateDirectory(dossier);
        var chemin = Path.Combine(dossier, $"{JournalFichierProvider.Prefixe}{DateTime.Now:yyyyMMdd}.log");
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
