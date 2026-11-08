using Pharma.Domain.Alertes;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Tests;

public class RegleAlerteTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Report_aujourd_hui_ou_dans_le_passe_refuse(int jours) =>
        Assert.Throws<RegleMetierException>(() => RegleAlerte.VerifierReport(Aujourdhui.AddDays(jours), Aujourdhui));

    [Fact]
    public void Report_a_demain_accepte() =>
        RegleAlerte.VerifierReport(Aujourdhui.AddDays(1), Aujourdhui);
}
