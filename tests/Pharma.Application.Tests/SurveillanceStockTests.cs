using Pharma.Application.Stock;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;
using Pharma.Application.Commun;

namespace Pharma.Application.Tests;

public class SurveillanceStockTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 11));

    [Fact]
    public async Task Boite_identifiee_comptee_une_seule_fois_par_inventaire()
    {
        var depot = new InventairesEnMemoire { BoiteScannee = 501 };
        var cas = new ScannerBoiteInventaire(depot, new DroitsFactices("stock.ajuster"));
        await cas.ExecuterAsync(5, "2000000000501", 3);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, "2000000000501", 3));
        Assert.Contains("déjà été comptée", ex.Message);
        Assert.Equal([501L], depot.BoitesVues);
    }

    [Fact]
    public async Task Echantillon_nominatif_jamais_pour_un_psychotrope_et_surveille()
    {
        var (sorties, surveillance) = (new SortiesEnMemoire(), new SurveillanceEnMemoire());
        var cas = new DeclarerSortieStock(sorties, new DroitsFactices("stock.donner"), new JournalEspion(), new Surveiller(surveillance, Horloge));

        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(9, 1, "echantillon", null, 3));
        Assert.Equal("commentaire_sortie", ex.Champ);

        await cas.ExecuterAsync(9, 2, "echantillon", "Dr Benali, visite médicale", 3);
        Assert.Equal(("echantillon", "Don : Dr Benali, visite médicale"), (sorties.Ecrite!.Motif, sorties.Ecrite.Reference));
        Assert.Single(surveillance.Calculs);

        sorties.Lot = sorties.Lot with { SousRegistre = true };
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(9, 1, "echantillon", "Dr Benali", 3));
    }

    [Fact]
    public async Task Don_reserve_au_droit_special_et_peremption_a_son_droit()
    {
        var sorties = new SortiesEnMemoire();
        var declarant = new DeclarerSortieStock(sorties, new DroitsFactices("stock.declarer_perime"), new JournalEspion());
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() => declarant.ExecuterAsync(9, 1, "echantillon", "Personne dans le besoin", 3));
        Assert.Equal("stock.donner", ex.Permission);

        var donneur = new DeclarerSortieStock(sorties, new DroitsFactices("stock.donner"), new JournalEspion());
        await Assert.ThrowsAsync<DroitRefuseException>(() => donneur.ExecuterAsync(9, 1, "perime", null, 3));
        await donneur.ExecuterAsync(9, 1, "echantillon", "Personne dans le besoin", 3);
        Assert.Equal("echantillon", sorties.Ecrite!.Motif);
    }

    [Fact]
    public async Task Peremption_declaree_sans_calcul_de_surveillance()
    {
        var (sorties, surveillance) = (new SortiesEnMemoire(), new SurveillanceEnMemoire());
        await new DeclarerSortieStock(sorties, new DroitsFactices("stock.declarer_perime"), new JournalEspion(), new Surveiller(surveillance, Horloge))
            .ExecuterAsync(9, 1, "perime", null, 3);
        Assert.Empty(surveillance.Calculs);
    }

    [Fact]
    public async Task Produits_sensibles_choisis_par_le_titulaire_seul()
    {
        var depot = new SurveillanceEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new GererProduitsSensibles(depot, new DroitsFactices("admin.parametres"), new JournalEspion()).MarquerAsync(18, true, 5));

        var journal = new JournalEspion();
        var gerer = new GererProduitsSensibles(depot, new DroitsFactices { Titulaire = true }, journal);
        await gerer.MarquerAsync(18, true, 1);
        Assert.Equal("CREME MAINS", (await gerer.ListerAsync(1)).Single().Nom);
        Assert.Equal(["produit_sensible"], journal.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MarquerAsync(999, true, 1));
    }

    [Fact]
    public async Task Comptage_des_produits_sensibles_avec_le_droit_d_inventaire()
    {
        var depot = new InventairesEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new CommencerInventaireSensibles(depot, new DroitsFactices("caisse.vendre")).ExecuterAsync(3));
        Assert.Equal(12, await new CommencerInventaireSensibles(depot, new DroitsFactices("stock.ajuster")).ExecuterAsync(3));
        Assert.Equal(2, (await new ObtenirTableauInventaire(depot).ExecuterAsync()).Sensibles!.Produits);
    }

    [Fact]
    public async Task Frequence_du_comptage_des_sensibles_controlee()
    {
        var depot = new SurveillanceEnMemoire();
        var regler = new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(depot.Parametres with { JoursComptageSensibles = 0 }, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(depot.Parametres with { SeuilCasses = 0 }, 1));
        await regler.EnregistrerAsync(depot.Parametres with { JoursComptageSensibles = 14, SeuilEchantillons = 2 }, 1);
        Assert.Equal((14, 2), (depot.Parametres.JoursComptageSensibles, depot.Parametres.SeuilEchantillons));
    }
}
