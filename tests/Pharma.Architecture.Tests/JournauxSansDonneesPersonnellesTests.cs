using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pharma.Api.Endpoints;
using Pharma.Api.Journalisation;
using Pharma.Api.Securite;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>Journaux sans données personnelles : masqueur, refus métier, code super admin, journal fichier.</summary>
public sealed class JournauxSansDonneesPersonnellesTests
{
    [Theory]
    [InlineData("assuré 690712345642/80 refusé", "assuré 6907******42/80 refusé")]
    [InlineData("assuré 123456789", "assuré 1234******89")]
    [InlineData("assuré 123456789012", "assuré 1234******12")]
    [InlineData("tél 0661234512", "tél 06******12")]
    [InlineData("tél 05 51 23 45 67", "tél 05******67")]
    [InlineData("tél 0771.23.45.98", "tél 07******98")]
    [InlineData("tél +213661234512", "tél +2136******12")]
    [InlineData("tél +213 6 61 23 45 12", "tél +2136******12")]
    [InlineData("tél 00213551234567", "tél 002135******67")]
    [InlineData("mail amine.benali@djezzy.dz", "mail a***@d***.dz")]
    [InlineData("mail x@pharmacie.officine.com fin", "mail x***@p***.com fin")]
    public void Masqueur_masque_chaque_format(string texte, string attendu) =>
        Assert.Equal(attendu, MasqueurJournal.Masquer(texte));

    [Theory]
    [InlineData("12 boîtes, lot 12345678, 2026-11-16 10:42:05.123")]
    [InlineData("Vente 1542 enregistrée")]
    public void Masqueur_laisse_les_textes_sans_donnees_personnelles(string texte) =>
        Assert.Equal(texte, MasqueurJournal.Masquer(texte));

    [Fact]
    public void Masqueur_masque_la_pile_d_exception()
    {
        var e = new InvalidOperationException("Envoi au 0661234512 impossible", new Exception("contact a.b@c.dz"));
        var texte = MasqueurJournal.Masquer(e);
        Assert.DoesNotContain("0661234512", texte);
        Assert.DoesNotContain("a.b@c.dz", texte);
        Assert.Contains("06******12", texte);
    }

    [Fact]
    public async Task Refus_metier_avec_un_nom_ne_laisse_pas_le_nom_au_journal()
    {
        var journal = new JournalMemoire();
        var http = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton<ILoggerFactory>(new LoggerFactory([journal])).BuildServiceProvider()
        };
        http.Request.Path = "/api/ventes";
        var ctx = EndpointFilterInvocationContext.Create(http);

        await ErreursMetier.Filtrer(ctx, _ => throw new RegleMetierException("Le patient Karim BENALI n'a pas de droits Chifa.", "assure"));

        var ligne = Assert.Single(journal.Lignes);
        Assert.DoesNotContain("BENALI", ligne);
        Assert.DoesNotContain("Karim", ligne);
        Assert.Contains("RegleMetierException", ligne);
        Assert.Contains("assure", ligne);
    }

    [Fact]
    public void Code_super_admin_ne_journalise_pas_le_destinataire()
    {
        var journal = new JournalMemoire();
        SuperAdminEndpoints.JournaliserCodeDemande(new LoggerFactory([journal]).CreateLogger("Pharma.SuperAdmin"), "restauration", "SMS");

        var ligne = Assert.Single(journal.Lignes);
        Assert.Contains("restauration", ligne);
        Assert.Contains("SMS", ligne);
        Assert.DoesNotContain("06", ligne);
        Assert.DoesNotContain("@", ligne);
    }

    [Fact]
    public void Journal_fichier_masque_message_et_exception()
    {
        var dossier = Path.Combine(Path.GetTempPath(), "journal-masque-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var fournisseur = new JournalFichierProvider(new OptionsJournalFichier { Dossier = dossier }))
                fournisseur.CreateLogger("Test").LogError(new Exception("mail a.b@c.dz"), "Envoi au {Tel}", "0661234512");
            var contenu = string.Concat(Directory.GetFiles(dossier).Select(File.ReadAllText));
            Assert.Contains("06******12", contenu);
            Assert.DoesNotContain("0661234512", contenu);
            Assert.DoesNotContain("a.b@c.dz", contenu);
        }
        finally
        {
            try { Directory.Delete(dossier, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Journal en mémoire : messages rendus, comme ils seraient écrits.</summary>
    private sealed class JournalMemoire : ILoggerProvider, ILogger
    {
        public List<string> Lignes { get; } = [];
        public ILogger CreateLogger(string categorie) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel niveau) => true;
        public void Log<TState>(LogLevel niveau, EventId id, TState etat, Exception? exception, Func<TState, Exception?, string> formateur) =>
            Lignes.Add(formateur(etat, exception) + (exception is null ? "" : " " + exception));
        public void Dispose() { }
    }
}
