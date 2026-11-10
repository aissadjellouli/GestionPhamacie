using Pharma.Application.Tracabilite;

namespace Pharma.Application.Tests;

public class TracabiliteTests
{
    [Fact]
    public async Task Code_vide_n_interroge_pas_la_base()
    {
        var lecture = new TracabiliteEspion();
        Assert.Null(await new ConsulterParcoursBoite(lecture).ExecuterAsync("  "));
        Assert.Null(lecture.CodeRecu);
    }

    [Fact]
    public async Task Code_nettoye_avant_recherche()
    {
        var lecture = new TracabiliteEspion();
        await new ConsulterParcoursBoite(lecture).ExecuterAsync(" 2000000001272 ");
        Assert.Equal("2000000001272", lecture.CodeRecu);
    }
}
