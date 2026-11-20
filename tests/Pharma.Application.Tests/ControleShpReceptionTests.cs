using Pharma.Application.Achats;
using Pharma.Domain.Achats;

namespace Pharma.Application.Tests;

/// <summary>
/// Contrôle du SHP à la réception (décision du 2026-11-15) : le SHP de la facture du grossiste est comparé au SHP de la classe du
/// produit (2,50 liste I, 1,50 liste II, 0 parapharmacie) ; un écart est signalé et journalisé, sans bloquer, et le lot garde le SHP facturé.
/// </summary>
public class ControleShpReceptionTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);

    private static (Receptionner Cas, AchatsEnMemoire Achats, JournalEspion Journal) Cas()
    {
        var achats = new AchatsEnMemoire();
        var journal = new JournalEspion();
        return (new Receptionner(achats, new DroitsFactices("achats.receptionner", "prix.modifier"), new HorlogeFixe(Aujourdhui), journal), achats, journal);
    }

    private static LigneReceptionSaisie Ligne(long produit, decimal? shp, string lot = "L1") =>
        new(produit, "Produit " + produit, lot, Aujourdhui.AddYears(1), 4, 100m, Shp: shp);

    [Theory]
    [InlineData("liste_i", 2.50)]
    [InlineData("liste_ii", 1.50)]
    [InlineData("libre", 0)]
    [InlineData("stupefiant", 2.50)]
    public void Shp_conforme_a_la_classe_rien_a_signaler(string classe, decimal shp) =>
        Assert.Null(RegleShp.Controler(1, "x", classe, shp, RegleShp.ParBoite(classe)));

    [Fact]
    public void Shp_non_saisi_rien_a_signaler() => Assert.Null(RegleShp.Controler(1, "x", "liste_i", null, RegleShp.ListeI));

    [Fact]
    public void Message_indique_la_shp_facture_la_shp_attendue_et_la_classe()
    {
        var e = RegleShp.Controler(1, "Périndopril", "libre", 2.50m, 0m)!;
        Assert.Contains("SHP facture 2,50 DA ≠ SHP attendue 0,00 DA pour un produit hors liste", e.Message.Replace('.', ','));
        Assert.Contains("vérifier la classe du produit ou la facture", e.Message);
    }

    [Fact]
    public async Task Shp_conforme_aucun_ecart_ni_journal()
    {
        var (cas, _, journal) = Cas();
        var r = await cas.ExecuterAsync(new SaisieReception(1, null, "BL-1", "F-1", [Ligne(1, 2.50m), Ligne(2, 1.50m)]), 5);
        Assert.Empty(r.EcartsShp);
        Assert.DoesNotContain("ecart_shp_reception", journal.Actions);
    }

    [Fact]
    public async Task Liste_I_facturee_a_1_50_signalee_non_bloquante_et_journalisee()
    {
        var (cas, achats, journal) = Cas();
        var r = await cas.ExecuterAsync(new SaisieReception(1, null, "BL-1", "F-1", [Ligne(1, 1.50m)]), 5);

        var e = Assert.Single(r.EcartsShp);
        Assert.Equal((1.50m, 2.50m, "liste_i"), (e.ShpFacture, e.ShpAttendu, e.Classe));
        // Non bloquant : la réception est enregistrée et le lot garde le SHP de la facture
        Assert.NotNull(achats.ReceptionRecue);
        Assert.Equal(1.50m, achats.ReceptionRecue!.Lignes[0].Shp);
        var i = journal.Actions.IndexOf("ecart_shp_reception");
        Assert.True(i >= 0);
        Assert.Equal(1.50m, journal.Details[i]["shp_facture"]);
        Assert.Equal(2.50m, journal.Details[i]["shp_attendue"]);
        Assert.Equal("liste_i", journal.Details[i]["classe"]);
        Assert.Equal(1L, journal.Details[i]["produit_id"]);
    }

    [Fact]
    public async Task Parapharmacie_facturee_a_2_50_signalee()
    {
        var (cas, achats, journal) = Cas();
        achats.Classes[3] = "libre";
        var r = await cas.ExecuterAsync(new SaisieReception(1, null, "BL-1", "F-1", [Ligne(3, 2.50m), Ligne(1, 2.50m, "L2")]), 5);

        var e = Assert.Single(r.EcartsShp);
        Assert.Equal((3L, 2.50m, 0m), (e.ProduitId, e.ShpFacture, e.ShpAttendu));
        Assert.Single(journal.Actions, a => a == "ecart_shp_reception");
        Assert.Equal(2.50m, achats.ReceptionRecue!.Lignes.Single(l => l.ProduitId == 3).Shp);
    }
}
