using Pharma.Application.Caisse;
using Pharma.Application.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Retour client d'une vente payée en partie par avoir (2026-11-15, D2, script 109) : l'avoir est recrédité d'abord, à hauteur de ce qui a
/// été payé par avoir et pas encore recrédité ; le reste suit le mode d'origine. Imposé par le serveur, inscrit au journal.
/// </summary>
public class RetourAvoirTests
{
    // T-005935 de la recette : 170 DA, dont 100 par avoir et 70 en espèces
    private static VenteRetournable Vente(int quantite = 1, decimal avoirDejaRecredite = 0, int dejaRendue = 0, decimal dejaRendu = 0) => new(
        new VenteAnnulable(35, "T-005935", DateTime.UtcNow, "Vendeur", "Patient fictif", 170, 170, 0, "especes", "validee", true, false, [],
            DejaRendu: dejaRendu, PatientId: 7, PayeParAvoir: 100, AvoirRecrediteParRetours: avoirDejaRecredite),
        [new LigneRetournable(1, 10, "Sirop fictif", "S1", quantite, dejaRendue, 170, [])]);

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock, JournalEspion Journal) Monter(VenteRetournable v)
    {
        var stock = new RetoursEnMemoire(v);
        var journal = new JournalEspion();
        var droits = new DroitsParEmploye(new() { [8] = ["caisse.vendre", "caisse.retour_especes"] });
        return (new EnregistrerRetourClient(stock, new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(new CodesEnMemoire(), new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 15)), journal), journal),
            stock, journal);
    }

    [Fact]
    public async Task L_avoir_est_recredite_d_abord_et_seul_le_reste_est_rendu_en_especes()
    {
        var (cas, stock, journal) = Monter(Vente());
        var r = await cas.ExecuterAsync(35, "Autre", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);

        Assert.Equal((100m, 70m, "especes"), (r.AvoirRecredite, r.ARendre, r.Mode));
        Assert.Equal((170m, 100m, 7L), (stock.Ecrit!.Montant, stock.Ecrit.AvoirRecredite, stock.Ecrit.PatientAvoirId));
        var detail = journal.Details[journal.Actions.IndexOf("retour_client")];
        Assert.Equal((100m, 70m), ((decimal)detail["avoir_recredite"]!, (decimal)detail["rendu"]!));
    }

    [Fact]
    public async Task Retours_successifs_l_avoir_n_est_jamais_recredite_deux_fois()
    {
        // Premier retour (1 boîte sur 2, 85 DA) : tout en avoir
        var (premier, _, _) = Monter(Vente(quantite: 2));
        var r1 = await premier.ExecuterAsync(35, "Autre", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((85m, 0m), (r1.AvoirRecredite, r1.ARendre));

        // Second retour : il reste 15 DA d'avoir à recréditer, 70 DA rendus en espèces
        var (second, _, _) = Monter(Vente(quantite: 2, avoirDejaRecredite: 85, dejaRendue: 1, dejaRendu: 85));
        var r2 = await second.ExecuterAsync(35, "Autre", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((15m, 70m), (r2.AvoirRecredite, r2.ARendre));
    }

    [Fact]
    public async Task Vente_sans_avoir_inchangee()
    {
        var v = Vente();
        var (cas, stock, _) = Monter(v with { Vente = v.Vente with { PayeParAvoir = 0 } });
        var r = await cas.ExecuterAsync(35, "Autre", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal((0m, 170m), (r.AvoirRecredite, r.ARendre));
        Assert.Null(stock.Ecrit!.PatientAvoirId);
    }

    [Theory]
    [InlineData(170, 100, 0, 100, 70)]
    [InlineData(50, 100, 0, 50, 0)]
    [InlineData(85, 100, 85, 15, 70)]
    [InlineData(85, 100, 100, 0, 85)]
    [InlineData(85, 0, 0, 0, 85)]
    public void Regle_retour_paye_par_avoir(decimal montant, decimal avoirUtilise, decimal deja, decimal avoirAttendu, decimal resteAttendu) =>
        Assert.Equal((avoirAttendu, resteAttendu), RegleAvoir.RetourPayeParAvoir(montant, avoirUtilise, deja));
}
