using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class RegleFefoTests
{
    private static readonly LotComptoir Recent = new(2, "B2", new DateOnly(2028, 6, 30), "ok");

    [Theory]
    [InlineData("rouge", true)]
    [InlineData("orange", true)]
    [InlineData("ok", false)]                     // périme avant, mais pas « proche » : pas d'avertissement
    [InlineData("risque_ecoulement", false)]
    public void Avertit_si_un_lot_qui_perime_avant_est_proche(string niveau, bool attendu) =>
        Assert.Equal(attendu, RegleFefo.Avertir(Recent, new(1, "A1", new DateOnly(2027, 1, 15), niveau)) is not null);

    [Fact]
    public void Pas_d_avertissement_pour_le_premier_lot_lui_meme_ou_un_lot_plus_recent()
    {
        Assert.Null(RegleFefo.Avertir(Recent, Recent));
        Assert.Null(RegleFefo.Avertir(Recent, new(3, "C3", new DateOnly(2029, 1, 1), "rouge")));
        Assert.Null(RegleFefo.Avertir(Recent, null));
    }

    [Fact]
    public void Message_indique_le_lot_a_servir_et_les_dates()
    {
        var m = RegleFefo.Avertir(Recent, new(1, "A1", new DateOnly(2027, 1, 15), "rouge"))!;
        Assert.Contains("A1", m);
        Assert.Contains("15/01/2027", m);
        Assert.Contains("30/06/2028", m);
    }
}
