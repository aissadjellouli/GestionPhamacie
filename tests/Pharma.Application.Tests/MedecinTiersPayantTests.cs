using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Tiers payant (2026-11-15) : médecin obligatoire quand l'organisme paie une partie ; part 0 ou volet Chifa : facultatif.</summary>
public class MedecinTiersPayantTests
{
    [Theory]
    [InlineData(true, 178.0, false, true, false)]   // l'organisme paie une partie (taux, ou reste saisi inférieur au total)
    [InlineData(true, 0.0, false, false, true)]     // part 0 : facultatif, validation de la vente sans ordonnance
    [InlineData(true, 178.0, true, false, false)]   // volet Chifa scanné
    [InlineData(false, 0.0, false, false, false)]   // vente libre
    public void Le_medecin_depend_de_la_part_de_l_organisme(bool tiersPayant, double part, bool volet, bool exige, bool sansPart)
    {
        Assert.Equal(exige, RegleMedecinTiersPayant.Exige(tiersPayant, (decimal)part, volet));
        Assert.Equal(sansPart, RegleMedecinTiersPayant.SansPartOrganisme(tiersPayant, (decimal)part, volet));
    }

    private static Encaisser Cas() =>
        new(new CatalogueEnMemoire(), new PatientsEnMemoire(Assure), new VentesEspion(), new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire());

    private static readonly Patient Assure = new() { Id = 3, Nom = "Fictif", NumAssure = "ASSURE-DEMO-3", OrganismeId = 1 };

    private static CommandeVente TiersPayant(decimal? reste = null) =>
        new([new LigneCommande(2, 20, 1)], true, 1, 80, Assure.Id, null, 0, ModePaiement.Especes, null, reste);

    [Fact]
    public async Task Au_taux_sans_medecin_le_serveur_refuse()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Cas().ExecuterAsync(TiersPayant(), 5, "Caisse 1"));
        Assert.Equal("nom_medecin", e.Champ);
        Assert.Contains("médecin prescripteur", e.Message);
    }

    [Fact]
    public async Task Reste_saisi_inferieur_au_total_l_organisme_paie_le_medecin_est_exige()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Cas().ExecuterAsync(TiersPayant(reste: 50), 5, "Caisse 1"));
        Assert.Equal("nom_medecin", e.Champ);
    }

    [Fact]
    public async Task Sans_part_de_l_organisme_le_medecin_est_facultatif_mais_la_vente_sans_ordonnance_est_validee()
    {
        // Reste à charge = tout le médicament (350 DA) : l'organisme ne paie rien ; sans le droit, la validation est demandée
        var e = await Record.ExceptionAsync(() => Cas().ExecuterAsync(TiersPayant(reste: 350), 5, "Caisse 1"));
        Assert.False(e is RegleMetierException { Champ: "nom_medecin" });
        var refus = Assert.IsType<DroitRefuseException>(e);
        Assert.Contains("tiers payant sans part", refus.Message);
    }
}
