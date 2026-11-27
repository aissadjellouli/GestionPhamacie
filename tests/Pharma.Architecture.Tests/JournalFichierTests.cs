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

    // ---- Dossier commun des journaux (décision du titulaire du 2026-11-28) : C:\Logs\PharmaOfficine\<application>, repli sur l'ancien dossier ----

    [Fact]
    public void Dossier_commun_sous_le_lecteur_du_systeme_et_nom_de_poste_sans_caractere_interdit()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Null(DossiersJournaux.Windows(DossiersJournaux.Api)); return; }
        var lecteur = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(Path.Combine(lecteur, "Logs", "PharmaOfficine", "Api"), DossiersJournaux.Windows(DossiersJournaux.Api));
        Assert.Equal(Path.Combine(lecteur, "Logs", "PharmaOfficine", "Central"), DossiersJournaux.Windows(DossiersJournaux.Central));
        Assert.Equal(Path.Combine(lecteur, "Logs", "PharmaOfficine", "Poste", "CAISSE_1"), DossiersJournaux.Windows(DossiersJournaux.Poste, "CAISSE:1"));
        Assert.Equal("inconnu", DossiersJournaux.NomSur("  "));
        Assert.Equal("PC", DossiersJournaux.NomSur("PC. "));
    }

    [Fact]
    public void Dossier_prefere_retenu_s_il_est_inscriptible_sinon_repli_sans_exception()
    {
        var prefere = Path.Combine(dossier, "prefere");
        var repli = Path.Combine(dossier, "repli");
        Assert.Equal(prefere, DossiersJournaux.Choisir(prefere, repli));
        Assert.True(Directory.Exists(prefere));
        Assert.Empty(Directory.GetFiles(prefere));   // le fichier d'essai d'écriture ne reste pas

        Assert.Equal(repli, DossiersJournaux.Choisir(null, repli));
        Assert.Equal(repli, DossiersJournaux.Choisir("  ", repli));
        // Dossier impossible à créer : un fichier porte déjà ce nom
        var occupe = Path.Combine(dossier, "occupe");
        File.WriteAllText(occupe, "x");
        Assert.Equal(repli, DossiersJournaux.Choisir(Path.Combine(occupe, "journaux"), repli));
        Assert.False(Directory.Exists(repli));        // le repli n'est pas créé ici : le journal le crée à sa première ligne
    }

    [Fact]
    public void Les_programmes_ecrivent_leurs_journaux_dans_le_dossier_commun_et_gardent_les_decisions_de_bascule_a_part()
    {
        var src = Path.Combine(BaseJetableM4.RacineDepot(), "src");
        var api = File.ReadAllText(Path.Combine(src, "Pharma.Api", "Program.cs"));
        Assert.Contains("DossiersJournaux.Windows(Pharma.Shared.Journalisation.DossiersJournaux.Api)", api);
        Assert.Contains("AjouterVigieSecondServeur(builder.Configuration, dossierDecisions)", api);   // fichiers relus au démarrage : pas dans C:\Logs
        foreach (var central in new[] { "Pharma.Cloud.Api", "Pharma.Cloud.Web" })
            Assert.Contains(@"""Dossier"": ""C:\\Logs\\PharmaOfficine\\Central""", File.ReadAllText(Path.Combine(src, central, "appsettings.json")));
        // Poste : plus aucun journal écrit directement dans %LOCALAPPDATA%
        var poste = Path.Combine(src, "Pharma.Desktop");
        Assert.Contains("JournauxPoste.Dossier", File.ReadAllText(Path.Combine(poste, "Services", "JournalLiaison.cs")));
        Assert.Contains("JournauxPoste.Dossier", File.ReadAllText(Path.Combine(poste, "App.xaml.cs")));
        Assert.Contains("JournauxPoste.Dossier", File.ReadAllText(Path.Combine(poste, "Services", "FichierTachesLaissees.cs")));
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
