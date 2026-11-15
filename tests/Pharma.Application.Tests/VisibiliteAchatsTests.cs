using Pharma.Application.Achats;

namespace Pharma.Application.Tests;

public class VisibiliteAchatsTests
{
    [Fact]
    public async Task Vendeur_sans_droit_ne_voit_ni_prix_d_achat_ni_dette_des_grossistes()
    {
        var v = await VisibiliteAchats.LireAsync(new DroitsFactices("achats.commander", "achats.receptionner"), 7);
        Assert.Equal((false, false), (v.PrixAchat, v.DetteGrossistes));
        Assert.Null(v.Prix(245m));
        Assert.Null(v.Prix((decimal?)245m));
        Assert.Null(v.Dette(194_436.49m));
    }

    [Fact]
    public async Task Droit_rapports_marges_montre_les_prix_et_achats_regler_la_dette()
    {
        var prix = await VisibiliteAchats.LireAsync(new DroitsFactices("rapports.marges"), 7);
        Assert.Equal((true, false), (prix.PrixAchat, prix.DetteGrossistes));
        Assert.Equal(245m, prix.Prix(245m));
        var dette = await VisibiliteAchats.LireAsync(new DroitsFactices("achats.regler"), 7);
        Assert.Equal((false, true), (dette.PrixAchat, dette.DetteGrossistes));
        Assert.Equal(15_000m, dette.Dette(15_000m));
    }

    [Fact]
    public async Task Titulaire_voit_tout()
    {
        var v = await VisibiliteAchats.LireAsync(new DroitsFactices() { Titulaire = true }, 1);
        Assert.Equal((true, true), (v.PrixAchat, v.DetteGrossistes));
    }
}
