using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Tests;

public class RegleLotTests
{
    private static readonly DateOnly Mars28 = new(2028, 3, 31);
    private static LotExistant Lot(DateOnly date, bool bloque = false, bool aVerifier = false, long id = 1) => new(id, date, aVerifier, 10, 150, bloque);

    [Theory]
    [InlineData(" am55 ", "AM55")]
    [InlineData("Lot-07b", "LOT-07B")]
    [InlineData(null, "")]
    public void Numero_de_lot_normalise(string? saisi, string attendu) => Assert.Equal(attendu, RegleLot.Normaliser(saisi));

    [Fact]
    public void Meme_lot_meme_date_complete_le_lot_sans_conflit()
    {
        var d = RegleLot.Decider([Lot(Mars28)], Mars28, new ChoixLot(), "Amlodipine", "AM55");
        Assert.Equal(1, d.Completer!.Id);
        Assert.Empty(RegleLot.Conflits([Lot(Mars28)], Mars28));
    }

    [Fact]
    public void Nouveau_lot_sans_conflit()
    {
        var d = RegleLot.Decider([], Mars28, new ChoixLot(), "Amlodipine", "AM55");
        Assert.Equal((null, false), (d.Completer, d.NouveauBloque));
    }

    [Fact]
    public void Date_differente_demande_un_choix_puis_lot_separe()
    {
        Assert.Equal([ConflitLot.DateDifferente], RegleLot.Conflits([Lot(Mars28)], Mars28.AddMonths(1)));
        var ex = Assert.Throws<RegleMetierException>(() => RegleLot.Decider([Lot(Mars28)], Mars28.AddMonths(1), new ChoixLot(), "Amlodipine", "AM55"));
        Assert.Contains("31/03/2028", ex.Message);
        Assert.Contains("lot séparé", ex.Message);

        var d = RegleLot.Decider([Lot(Mars28)], Mars28.AddMonths(1), new ChoixLot(LotSepare: true), "Amlodipine", "AM55");
        Assert.Null(d.Completer);                       // nouveau lot avec la date saisie
    }

    [Fact]
    public void Date_provisoire_de_la_reprise_remplacee_par_la_livraison() =>
        Assert.Equal(1, RegleLot.Decider([Lot(Mars28, aVerifier: true)], Mars28.AddMonths(5), new ChoixLot(), "Amlodipine", "AM55").Completer!.Id);

    [Fact]
    public void Lot_bloque_demande_un_choix_puis_entre_bloque()
    {
        Assert.Equal([ConflitLot.LotBloque], RegleLot.Conflits([Lot(Mars28, bloque: true)], Mars28));
        Assert.Throws<RegleMetierException>(() => RegleLot.Decider([Lot(Mars28, bloque: true)], Mars28, new ChoixLot(), "Vitamine D3", "VD3"));
        var d = RegleLot.Decider([Lot(Mars28, bloque: true)], Mars28, new ChoixLot(EntrerBloque: true), "Vitamine D3", "VD3");
        Assert.Equal(1, d.Completer!.Id);               // complète le lot bloqué, qui reste bloqué
    }

    [Fact]
    public void Lot_rappele_avec_autre_date_cumule_les_deux_choix_et_cree_un_lot_bloque()
    {
        var lots = new[] { Lot(Mars28, bloque: true) };
        Assert.Equal([ConflitLot.DateDifferente, ConflitLot.LotBloque], RegleLot.Conflits(lots, Mars28.AddDays(1)));
        Assert.Throws<RegleMetierException>(() => RegleLot.Decider(lots, Mars28.AddDays(1), new ChoixLot(LotSepare: true), "X", "L1"));
        var d = RegleLot.Decider(lots, Mars28.AddDays(1), new ChoixLot(LotSepare: true, EntrerBloque: true), "X", "L1");
        Assert.Equal((null, true), (d.Completer, d.NouveauBloque));
    }
}
