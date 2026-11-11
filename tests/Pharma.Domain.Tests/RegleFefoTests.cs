using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class RegleFefoTests
{
    private static readonly LotComptoir Recent = new(2, "B2", new DateOnly(2028, 6, 30), "ok");

    [Theory]
    [InlineData("rouge", "rouge")]
    [InlineData("orange", "rouge")]
    [InlineData("ok", "orange")]                  // périme avant sans être proche : alerte orange
    [InlineData("risque_ecoulement", "orange")]
    public void Avertit_toujours_rouge_si_le_lot_plus_ancien_est_proche_orange_sinon(string niveau, string couleur)
    {
        var ancien = new LotComptoir(1, "A1", new DateOnly(2027, 1, 15), niveau);
        Assert.NotNull(RegleFefo.Avertir(Recent, ancien));
        Assert.Equal(couleur, RegleFefo.Couleur(ancien));
    }

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
