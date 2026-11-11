using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Surveillance;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class ComptagesEnMemoire : IComptagesReception
{
    public Dictionary<long, ComptageReception> Comptages { get; } = [];
    public List<(long ReceptionId, IReadOnlyList<(long LotId, int Boites)> Manquants)> Sorties { get; } = [];

    public Task<long> CreerAsync(long f, string bl, IReadOnlyList<LigneComptee> lignes, IReadOnlyList<long> scans, long u, CancellationToken ct = default)
    {
        var id = Comptages.Count + 1L;
        Comptages[id] = new ComptageReception(id, f, $"Grossiste {f}", bl, StatutsComptage.Compte, u, $"Employé {u}", DateTime.UtcNow,
            null, null, null, lignes, scans, null, [], 0, 0, 0);
        return Task.FromResult(id);
    }
    public Task<ComptageReception?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Comptages.GetValueOrDefault(id));
    public Task<IReadOnlyList<ComptageReception>> EnCoursAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ComptageReception>>(Comptages.Values.Where(c => c.Statut is "compte" or "ecart").ToList());
    public Task<bool> ChangerStatutAsync(long id, string attendu, string nouveau, long? facturePar, long? decidePar, CancellationToken ct = default)
    {
        if (Comptages[id].Statut != attendu) return Task.FromResult(false);
        Comptages[id] = Comptages[id] with { Statut = nouveau, FacturePar = facturePar ?? Comptages[id].FacturePar };
        return Task.FromResult(true);
    }
    public Task<bool> EnregistrerEcartAsync(long id, string saisie, FusionComptage fusion, decimal valeur, long facturePar, CancellationToken ct = default)
    {
        if (Comptages[id].Statut != StatutsComptage.Compte) return Task.FromResult(false);
        Comptages[id] = Comptages[id] with { Statut = StatutsComptage.Ecart, Saisie = saisie, Ecarts = fusion.Ecarts, FacturePar = facturePar,
            BoitesManquantes = fusion.BoitesManquantes, BoitesEnTrop = fusion.BoitesEnTrop, ValeurManquante = valeur };
        return Task.FromResult(true);
    }
    public Task LierReceptionAsync(long id, long receptionId, CancellationToken ct = default) => Task.CompletedTask;
    public Task<decimal> SortirManquantsAsync(long receptionId, string bl, IReadOnlyList<(long LotId, int Boites)> manquants, long u, CancellationToken ct = default)
    {
        Sorties.Add((receptionId, manquants));
        return Task.FromResult(manquants.Sum(m => m.Boites * 10m));
    }
}

public class ComptageReceptionTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);
    private static readonly HorlogeFixe Horloge = new(Aujourdhui);

    private static LigneComptee Compte(int quantite) => new(1, "Produit 1", "L1", Aujourdhui.AddYears(1), quantite);
    private static SaisieReception Facture(int quantite, Pharma.Application.Catalogue.ValidationBlocage? blocage = null) =>
        new(1, null, "BL-X", "F-1", [new LigneReceptionSaisie(1, "Produit 1", "L1", Aujourdhui.AddYears(1), quantite, 50m)], Blocage: blocage);

    private static (ComptagesEnMemoire, AchatsEnMemoire, long) Compte1(int boites)
    {
        var (comptages, achats) = (new ComptagesEnMemoire(), new AchatsEnMemoire());
        var id = comptages.CreerAsync(1, "BL-7", [Compte(boites)], [], 3).Result;
        return (comptages, achats, id);
    }

    private static Receptionner Receptionner(AchatsEnMemoire achats, SurveillanceEnMemoire? surveillance = null, params string[] droits) =>
        new(achats, new DroitsFactices(droits.Length > 0 ? droits : ["achats.receptionner"]), Horloge, new JournalEspion(),
            mode: surveillance is null ? null : new ModeReception(surveillance));

    [Fact]
    public async Task Mode_aveugle_la_reception_directe_est_refusee()
    {
        var surveillance = new SurveillanceEnMemoire();
        var achats = new AchatsEnMemoire();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Receptionner(achats, surveillance).ExecuterAsync(Facture(2), 5));
        Assert.Contains("comptez d’abord", ex.Message);

        surveillance.Parametres = surveillance.Parametres with { ReceptionAveugle = false };
        await Receptionner(achats, surveillance).ExecuterAsync(Facture(2), 5);
        Assert.NotNull(achats.ReceptionRecue);
    }

    [Fact]
    public async Task Comptage_controle_et_journalise()
    {
        var (comptages, journal) = (new ComptagesEnMemoire(), new JournalEspion());
        var compter = new CompterLivraison(comptages, new AchatsEnMemoire(), new DroitsFactices("achats.receptionner"), Horloge, journal);
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CompterLivraison(comptages, new AchatsEnMemoire(), new DroitsFactices("caisse.vendre"), Horloge, journal).ExecuterAsync(1, "BL", [Compte(1)], null, 3));
        await Assert.ThrowsAsync<RegleMetierException>(() => compter.ExecuterAsync(9, "BL", [Compte(1)], null, 3));
        var id = await compter.ExecuterAsync(1, " BL-9 ", [Compte(4) with { NumeroLot = " l1 " }], null, 3);
        Assert.Equal(("BL-9", "L1"), (comptages.Comptages[id].NumeroBl, comptages.Comptages[id].Lignes.Single().NumeroLot));
        Assert.Equal(["comptage_reception"], journal.Actions);
    }

    [Fact]
    public async Task Facture_conforme_au_comptage_la_livraison_entre_en_stock()
    {
        var (comptages, achats, id) = Compte1(4);
        var surveillance = new SurveillanceEnMemoire();
        var r = await new FacturerComptage(comptages, Receptionner(achats, surveillance), new DroitsFactices("achats.receptionner"), Horloge,
            new JournalEspion(), new Surveiller(surveillance, Horloge)).ExecuterAsync(id, Facture(4), 5);
        Assert.NotNull(r.Reception);
        Assert.Equal("BL-7", achats.ReceptionRecue!.NumeroBl);      // le BL du comptage
        Assert.Equal(StatutsComptage.Receptionne, comptages.Comptages[id].Statut);
        Assert.Single(surveillance.Calculs);                         // comptage et facture par la même personne ?
        await Assert.ThrowsAsync<RegleMetierException>(() => new FacturerComptage(comptages, Receptionner(achats), new DroitsFactices("achats.receptionner"),
            Horloge, new JournalEspion()).ExecuterAsync(id, Facture(4), 5));
    }

    [Fact]
    public async Task Ecart_rien_n_entre_en_stock_et_la_facture_est_gardee_sans_code_comptoir()
    {
        var (comptages, achats, id) = Compte1(3);
        var journal = new JournalEspion();
        var r = await new FacturerComptage(comptages, Receptionner(achats), new DroitsFactices("achats.receptionner"), Horloge, journal)
            .ExecuterAsync(id, Facture(5, new Pharma.Application.Catalogue.ValidationBlocage(true, "titulaire", "1234")), 5);
        Assert.Null(r.Reception);
        Assert.Null(achats.ReceptionRecue);
        var c = comptages.Comptages[id];
        Assert.Equal((StatutsComptage.Ecart, 2, 100m), (c.Statut, c.BoitesManquantes, c.ValeurManquante));
        Assert.DoesNotContain("1234", c.Saisie!);
        Assert.Equal(["ecart_reception"], journal.Actions);
    }

    [Fact]
    public async Task Ecart_valide_par_un_employe_habilite_titulaire_prevenu()
    {
        var (comptages, achats, id) = Compte1(3);
        await new FacturerComptage(comptages, Receptionner(achats), new DroitsFactices("achats.receptionner"), Horloge, new JournalEspion())
            .ExecuterAsync(id, Facture(5), 5);

        var alertes = new AlertesTitulaireEspion();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new DeciderEcartReception(comptages, Receptionner(achats),
            new DroitsFactices("achats.receptionner"), new JournalEspion(), alertes).ExecuterAsync(id, true, 6));

        var journal = new JournalEspion();
        var r = await new DeciderEcartReception(comptages, Receptionner(achats, null, "achats.receptionner", "achats.valider_ecart"),
            new DroitsFactices("achats.receptionner", "achats.valider_ecart"), journal, alertes).ExecuterAsync(id, true, 6);
        Assert.NotNull(r);
        Assert.Equal(5, achats.ReceptionRecue!.Lignes.Single().Quantite);           // dette selon la facture
        Assert.Equal([(100L, 2)], comptages.Sorties.Single().Manquants);            // 2 boîtes manquantes sorties du stock
        Assert.Equal(StatutsComptage.Receptionne, comptages.Comptages[id].Statut);
        Assert.Contains("ecart_reception_valide", journal.Actions);
        Assert.True(alertes.Surveillances.Single().Urgent);
        await Assert.ThrowsAsync<RegleMetierException>(() => new DeciderEcartReception(comptages, Receptionner(achats),
            new DroitsFactices("achats.valider_ecart"), new JournalEspion()).ExecuterAsync(id, true, 6));
    }

    [Fact]
    public async Task Ecart_renvoye_au_recomptage_par_le_titulaire_sans_alerte()
    {
        var (comptages, achats, id) = Compte1(3);
        await new FacturerComptage(comptages, Receptionner(achats), new DroitsFactices("achats.receptionner"), Horloge, new JournalEspion())
            .ExecuterAsync(id, Facture(5), 5);
        var alertes = new AlertesTitulaireEspion();
        var r = await new DeciderEcartReception(comptages, Receptionner(achats), new DroitsFactices("achats.valider_ecart") { Titulaire = true },
            new JournalEspion(), alertes).ExecuterAsync(id, false, 1);
        Assert.Null(r);
        Assert.Null(achats.ReceptionRecue);
        Assert.Equal(StatutsComptage.ARecompter, comptages.Comptages[id].Statut);
        Assert.Empty(alertes.Surveillances);
    }

    [Fact]
    public async Task Retour_sans_avoir_delai_controle()
    {
        var depot = new SurveillanceEnMemoire();
        var regler = new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => regler.EnregistrerAsync(depot.Parametres with { JoursAvoirRetour = 0 }, 1));
        await regler.EnregistrerAsync(depot.Parametres with { JoursAvoirRetour = 45, ReceptionAveugle = false }, 1);
        Assert.Equal((45, false), (depot.Parametres.JoursAvoirRetour, depot.Parametres.ReceptionAveugle));
    }
}
