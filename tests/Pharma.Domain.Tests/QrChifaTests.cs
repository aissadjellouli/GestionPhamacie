using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class QrChifaTests
{
    // QR lu sur un vrai volet Chifa (VIT D3, 3 boîtes à 156,48 DA)
    private const string Volet = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;";

    [Fact]
    public void Volet_reel_lu_champ_par_champ()
    {
        Assert.True(QrChifa.TryParse(Volet, out var qr));
        Assert.Equal(("06930126", new DateOnly(2026, 11, 11), "690755003842/80"), (qr!.NumeroFacture, qr.DateFacture, qr.AssureComplet));
        Assert.Equal(("24", "2", new DateOnly(2026, 11, 9)), (qr.Champ5, qr.Champ6, qr.DateSecondaire));
        Assert.Equal(new LigneQrChifa("07607", 3, 156.48m), qr.Lignes.Single());
        Assert.Equal(469.44m, qr.MontantOrdonnance);              // « Montant Ord » imprimé sur le volet
    }

    [Fact]
    public void Plusieurs_lignes_acceptees()
    {
        Assert.True(QrChifa.TryParse("00000001;11/11/2026;123456789012;00;1;1;;07607,3,156.48;01234,1,386.94", out var qr));
        Assert.Equal(2, qr!.Lignes.Count);
        Assert.Null(qr.DateSecondaire);
    }

    [Theory]
    [InlineData("")]
    [InlineData("26/F-015695")]                                            // QR d'une facture de grossiste
    [InlineData("06930126;11/11/2026;690755003842;80;24;2;09/11/2026")]     // sans ligne
    [InlineData("06930126;31/02/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48")]
    [InlineData("06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,0,156.48")]
    [InlineData("06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,abc")]
    public void Autre_contenu_refuse(string texte) => Assert.False(QrChifa.TryParse(texte, out _));
}
