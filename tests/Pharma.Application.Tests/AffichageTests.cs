using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class PreferencesEnMemoire : IPreferencesAffichage
{
    public Dictionary<long, string> Choix { get; } = [];
    public Task<string> LireAsync(long u, CancellationToken ct = default) => Task.FromResult(Choix.GetValueOrDefault(u, "auto"));
    public Task EnregistrerAsync(long u, string a, CancellationToken ct = default) { Choix[u] = a; return Task.CompletedTask; }
}

public class AffichageTests
{
    [Fact]
    public async Task Chaque_employe_garde_son_affichage_et_auto_par_defaut()
    {
        var p = new PreferencesEnMemoire();
        await new ChoisirAffichage(p).ExecuterAsync(1, "sombre");
        await new ChoisirAffichage(p).ExecuterAsync(3, "clair");
        Assert.Equal(("sombre", "clair", "auto"),
            (await new LireAffichage(p).ExecuterAsync(1), await new LireAffichage(p).ExecuterAsync(3), await new LireAffichage(p).ExecuterAsync(9)));
    }

    [Fact]
    public async Task Affichage_inconnu_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new ChoisirAffichage(new PreferencesEnMemoire()).ExecuterAsync(1, "noir"));
}
