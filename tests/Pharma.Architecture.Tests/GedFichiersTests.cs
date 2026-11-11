using System.Text;
using System.Text.RegularExpressions;
using Pharma.Application.Ged;
using Pharma.Infrastructure.Ged;

namespace Pharma.Architecture.Tests;

/// <summary>GED sur disque : dépôt identique ignoré, nouvelle version sous le nom simple, anciennes versions datées dans Versions.</summary>
public sealed class GedFichiersTests : IDisposable
{
    private readonly string racine = Path.Combine(Path.GetTempPath(), "ged-tests-" + Guid.NewGuid().ToString("N"));
    private readonly GedFichiers ged;

    public GedFichiersTests() => ged = new GedFichiers(new ReglagesGed(racine));

    public void Dispose()
    {
        try { Directory.Delete(racine, recursive: true); } catch (IOException) { }
    }

    private Task<string> Deposer(string nom, string contenu) => ged.DeposerAsync("Factures", nom, Encoding.UTF8.GetBytes(contenu));
    private string Dossier => Path.Combine(racine, "Factures");
    private string[] Versions => Directory.Exists(Path.Combine(Dossier, GedFichiers.SousDossierVersions))
        ? Directory.GetFiles(Path.Combine(Dossier, GedFichiers.SousDossierVersions)).Select(f => Path.GetFileName(f)).Order().ToArray()
        : [];

    [Fact]
    public async Task Premier_depot_sous_son_nom()
    {
        Assert.Equal(Path.Combine("Factures", "F-1.pdf"), await Deposer("F-1.pdf", "A"));
        Assert.Equal("A", File.ReadAllText(Path.Combine(Dossier, "F-1.pdf")));
        Assert.Empty(Versions);
    }

    [Fact]
    public async Task Depot_identique_n_ajoute_rien()
    {
        await Deposer("F-1.pdf", "A");
        Assert.Equal(Path.Combine("Factures", "F-1.pdf"), await Deposer("F-1.pdf", "A"));
        Assert.Single(Directory.GetFiles(Dossier));   // ni copie « (2) », ni fichier temporaire laissé
        Assert.Empty(Versions);
    }

    [Fact]
    public async Task Contenu_different_ancienne_version_datee_dans_Versions()
    {
        await Deposer("F-1.pdf", "A");
        await Deposer("F-1.pdf", "B");
        await Deposer("F-1.pdf", "C plus long");

        Assert.Equal("C plus long", File.ReadAllText(Path.Combine(Dossier, "F-1.pdf")));
        var versions = Versions;
        Assert.Equal(2, versions.Length);
        // « F-1 AAAA-MM-JJ HHhmm.pdf », puis « (2) » pour une seconde version dans la même minute
        Assert.All(versions, v => Assert.Matches(new Regex(@"^F-1 \d{4}-\d{2}-\d{2} \d{2}h\d{2}( \(\d+\))?\.pdf$"), v));
        var contenus = versions.Select(v => File.ReadAllText(Path.Combine(Dossier, GedFichiers.SousDossierVersions, v))).Order().ToArray();
        Assert.Equal(["A", "B"], contenus);
        Assert.Single(Directory.GetFiles(Dossier));
    }

    [Fact]
    public async Task Meme_taille_contenu_different_est_une_nouvelle_version()
    {
        await Deposer("F-1.pdf", "AAAA");
        await Deposer("F-1.pdf", "BBBB");   // même taille : l'empreinte tranche
        Assert.Equal("BBBB", File.ReadAllText(Path.Combine(Dossier, "F-1.pdf")));
        Assert.Single(Versions);
    }
}
