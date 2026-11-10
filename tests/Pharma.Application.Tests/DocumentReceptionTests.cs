using Pharma.Application.Achats;

namespace Pharma.Application.Tests;

public class DocumentReceptionTests
{
    [Theory]
    [InlineData(" 26/F-015695 ", "26/F-015695")]        // QR code Qouds Para (bon de livraison)
    [InlineData("26/FA076277", "26/FA076277")]           // QR code KR Distrimed (facture)
    [InlineData("é-!F)à&(-ç(", "26/F-015695")]          // douchette réglée en QWERTY sur un poste AZERTY (QR Qouds Para)
    [InlineData("é-!FQàè-éèè", "26/FA076277")]          // idem, QR KR Distrimed : A et Q inversés aussi
    [InlineData("Réception", "Réception")]                // texte ordinaire : laissé tel quel
    [InlineData("FA.26-327890ZZ1ZZ1", "FA.26-327890ZZ1ZZ1")] // contient des chiffres : laissé tel quel
    [InlineData("  ", null)]
    public void Le_numero_scanne_est_relu(string saisi, string? attendu) => Assert.Equal(attendu, RegleDocumentReception.Normaliser(saisi));

    [Fact]
    public async Task Une_reception_qui_porte_deja_ce_numero_est_signalee()
    {
        var c = new ChercherDocumentReception(new AchatsEnMemoire(), new DroitsFactices("achats.receptionner"));
        Assert.Single(await c.ExecuterAsync(1, " 26/F-015695 ", 9));
        Assert.Empty(await c.ExecuterAsync(1, "26/F-999999", 9));
        Assert.Empty(await c.ExecuterAsync(0, "26/F-015695", 9));   // grossiste pas encore choisi
    }
}
