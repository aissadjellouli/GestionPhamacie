using System.Reflection;
using System.Xml.Linq;

namespace Pharma.Architecture.Tests;

/// <summary>Règles de la Clean Architecture : les dépendances pointent vers le domaine, jamais l'inverse.</summary>
public class DependancesTests
{
    private static readonly Assembly Domain = typeof(Pharma.Domain.Commun.Ean13).Assembly;
    private static readonly Assembly Application = typeof(Pharma.Application.DependencyInjection).Assembly;
    private static readonly Assembly Infrastructure = typeof(Pharma.Infrastructure.DependencyInjection).Assembly;
    private static readonly Assembly Shared = typeof(Pharma.Shared.ErreurDto).Assembly;

    private static string[] References(Assembly a) =>
        a.GetReferencedAssemblies().Select(r => r.Name ?? "").ToArray();

    private static string[] ReferencesPharma(Assembly a) =>
        References(a).Where(n => n.StartsWith("Pharma.", StringComparison.Ordinal)).Order().ToArray();

    [Fact]
    public void Domain_ne_depend_de_rien() =>
        Assert.Empty(ReferencesPharma(Domain));

    [Fact]
    public void Shared_ne_depend_de_rien() =>
        Assert.Empty(ReferencesPharma(Shared));

    [Fact]
    public void Application_ne_depend_que_du_domaine() =>
        Assert.Equal(["Pharma.Domain"], ReferencesPharma(Application));

    [Theory]
    [InlineData("Npgsql")]
    [InlineData("Microsoft.EntityFrameworkCore")]
    [InlineData("Microsoft.AspNetCore")]
    [InlineData("BCrypt.Net-Next")]
    public void Domain_et_Application_sans_technique(string prefixe)
    {
        Assert.DoesNotContain(References(Domain), n => n.StartsWith(prefixe, StringComparison.Ordinal));
        Assert.DoesNotContain(References(Application), n => n.StartsWith(prefixe, StringComparison.Ordinal));
    }

    [Fact]
    public void Infrastructure_ignore_l_api_et_le_contrat_http()
    {
        var refs = ReferencesPharma(Infrastructure);
        Assert.DoesNotContain("Pharma.Api", refs);
        Assert.DoesNotContain("Pharma.Shared", refs);
        Assert.DoesNotContain(References(Infrastructure), n => n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void Desktop_ne_connait_que_le_contrat_http()
    {
        // Le poste cible Windows : lecture de son fichier projet plutôt que de son assembly.
        var csproj = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Pharma.Desktop.csproj");
        var references = XDocument.Load(csproj).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension((string?)e.Attribute("Include") ?? ""))
            .ToArray();
        Assert.Equal(["Pharma.Shared"], references);
    }

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
