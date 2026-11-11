using Pharma.Infrastructure.Sauvegardes;

namespace Pharma.Architecture.Tests;

/// <summary>Restauration de la GED sur disque : rien n'est supprimé, un document plus récent au serveur est gardé.</summary>
public sealed class RestaurationGedTests : IDisposable
{
    private readonly string racine = Path.Combine(Path.GetTempPath(), "restauration-ged-" + Guid.NewGuid().ToString("N"));
    private string Source => Path.Combine(racine, "externe", "GED");
    private string Serveur => Path.Combine(racine, "serveur");

    public void Dispose()
    {
        try { Directory.Delete(racine, recursive: true); } catch (IOException) { }
    }

    private static void Ecrire(string chemin, string contenu, DateTime date)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(chemin)!);
        File.WriteAllText(chemin, contenu);
        File.SetLastWriteTimeUtc(chemin, date);
    }

    [Fact]
    public void Documents_manquants_ou_plus_anciens_recopies_jamais_les_plus_recents()
    {
        var hier = DateTime.UtcNow.AddDays(-1);
        Ecrire(Path.Combine(Source, "Factures", "F-1.pdf"), "sauvegarde", hier);               // absent au serveur
        Ecrire(Path.Combine(Source, "Factures", "F-2.pdf"), "sauvegarde", hier);               // plus récent au serveur
        Ecrire(Path.Combine(Source, "Rapports", "R-1.pdf"), "sauvegarde", hier);               // plus ancien au serveur
        Ecrire(Path.Combine(Source, "Sauvegardes", "pharmacie_1.dump"), "base", hier);          // jamais recopié
        Ecrire(Path.Combine(Serveur, "Factures", "F-2.pdf"), "serveur récent", DateTime.UtcNow);
        Ecrire(Path.Combine(Serveur, "Rapports", "R-1.pdf"), "serveur ancien", hier.AddDays(-5));
        Ecrire(Path.Combine(Serveur, "Retours", "seul-au-serveur.pdf"), "serveur", hier);

        var copies = new StockagePgDump("Host=localhost;Database=x;Username=x", null).RestaurerDocuments(Source, Serveur);

        Assert.Equal(2, copies);
        Assert.Equal("sauvegarde", File.ReadAllText(Path.Combine(Serveur, "Factures", "F-1.pdf")));
        Assert.Equal("serveur récent", File.ReadAllText(Path.Combine(Serveur, "Factures", "F-2.pdf")));
        Assert.Equal("sauvegarde", File.ReadAllText(Path.Combine(Serveur, "Rapports", "R-1.pdf")));
        Assert.True(File.Exists(Path.Combine(Serveur, "Retours", "seul-au-serveur.pdf")));
        Assert.False(Directory.Exists(Path.Combine(Serveur, "Sauvegardes")));
        Assert.Empty(Directory.GetFiles(Serveur, "*.partiel", SearchOption.AllDirectories));
    }

    [Fact]
    public void Copie_de_securite_avant_restauration_jamais_supprimee_par_la_retention()
    {
        var dossier = Path.Combine(racine, "Sauvegardes");
        var ancien = DateTime.UtcNow.AddDays(-30);
        Ecrire(Path.Combine(dossier, "pharmacie_20261001_220000.dump"), "x", ancien);
        Ecrire(Path.Combine(dossier, "pharmacie_avant_restauration_20261001_120000.dump"), "x", ancien);

        var supprimes = new StockagePgDump("Host=localhost;Database=x;Username=x", null).SupprimerAvant(dossier, DateTime.Now.AddDays(-7));

        Assert.Equal(1, supprimes);
        Assert.True(File.Exists(Path.Combine(dossier, "pharmacie_avant_restauration_20261001_120000.dump")));
    }
}
