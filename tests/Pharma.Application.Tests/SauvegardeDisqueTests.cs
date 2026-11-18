using Pharma.Application.Sauvegardes;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Alerte « aucune sauvegarde réussie sur le disque externe depuis N jours » (décision du titulaire du 2026-11-18, script 148).</summary>
public class SauvegardeDisqueTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 18, 21, 0, 0, DateTimeKind.Utc);

    private static (SauvegardesEnMemoire Depot, AlertesTitulaireEspion Alertes, SurveillerSauvegardeDisque Surveiller) Creer(
        DateTime? derniereReussie, DateTime? suiviDepuis = null, bool actif = true, int jours = 7, DateTime? derniereAlerte = null)
    {
        var depot = new SauvegardesEnMemoire { DerniereReussie = derniereReussie };
        depot.Reglages = depot.Reglages with { Actif = actif, AlerteSansDisqueJours = jours, SuiviDisqueDepuisUtc = suiviDepuis ?? Maintenant.AddDays(-30),
            DerniereAlerteSansDisqueUtc = derniereAlerte };
        var alertes = new AlertesTitulaireEspion();
        return (depot, alertes, new SurveillerSauvegardeDisque(depot, alertes, new HorlogeReglable(Maintenant)));
    }

    [Fact]
    public async Task Sauvegarde_recente_aucune_alerte()
    {
        var (_, alertes, surveiller) = Creer(Maintenant.AddDays(-2));
        Assert.False(await surveiller.ExecuterAsync());
        Assert.Empty(alertes.SansDisque);
    }

    [Fact]
    public async Task Derniere_sauvegarde_de_plus_de_7_jours_titulaire_prevenu_une_fois_par_periode()
    {
        var (depot, alertes, surveiller) = Creer(Maintenant.AddDays(-8));
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Equal(7, alertes.SansDisque.Single().Jours);
        Assert.Contains("10/11/2026", alertes.SansDisque.Single().Detail);
        Assert.Equal(Maintenant, depot.Reglages.DerniereAlerteSansDisqueUtc);
        Assert.False(await surveiller.ExecuterAsync());   // même période : pas de seconde alerte
        Assert.Single(alertes.SansDisque);
    }

    [Fact]
    public async Task Alerte_renvoyee_apres_une_nouvelle_periode()
    {
        var (_, alertes, surveiller) = Creer(Maintenant.AddDays(-20), derniereAlerte: Maintenant.AddDays(-7));
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Single(alertes.SansDisque);
    }

    [Fact]
    public async Task Alerte_anterieure_a_la_derniere_reussite_ne_bloque_pas()
    {
        // Alerte il y a 10 jours, sauvegarde réussie il y a 9 jours, puis plus rien : nouvelle alerte dès 7 jours après la réussite
        var (_, alertes, surveiller) = Creer(Maintenant.AddDays(-9), derniereAlerte: Maintenant.AddDays(-10));
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Single(alertes.SansDisque);
    }

    [Fact]
    public async Task Jamais_de_sauvegarde_compte_depuis_le_debut_du_suivi()
    {
        var (_, alertes, surveiller) = Creer(null, suiviDepuis: Maintenant.AddDays(-3));
        Assert.False(await surveiller.ExecuterAsync());
        (_, alertes, surveiller) = Creer(null, suiviDepuis: Maintenant.AddDays(-7));
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Contains("Aucune sauvegarde réussie", alertes.SansDisque.Single().Detail);
    }

    [Fact]
    public async Task Sauvegarde_automatique_desactivee_alerte_quand_meme()
    {
        var (_, alertes, surveiller) = Creer(Maintenant.AddDays(-8), actif: false);
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Contains("désactivée", alertes.SansDisque.Single().Detail);
    }

    [Fact]
    public async Task Delai_regle_respecte()
    {
        var (_, alertes, surveiller) = Creer(Maintenant.AddDays(-8), jours: 14);
        Assert.False(await surveiller.ExecuterAsync());
        Assert.Empty(alertes.SansDisque);
    }

    [Fact]
    public async Task Delai_entre_1_et_90_jours_et_journalise()
    {
        var depot = new SauvegardesEnMemoire();
        var journal = new JournalEspion();
        var modifier = new ModifierReglagesSauvegarde(depot, journal);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            modifier.ExecuterAsync(true, @"E:\Sauvegardes", new TimeOnly(22, 0), 2, 1, default, null, null, 0));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            modifier.ExecuterAsync(true, @"E:\Sauvegardes", new TimeOnly(22, 0), 2, 1, default, null, null, 91));
        await modifier.ExecuterAsync(true, @"E:\Sauvegardes", new TimeOnly(22, 0), 2, 1, default, null, null, 10);
        Assert.Equal(10, depot.Reglages.AlerteSansDisqueJours);
        Assert.Equal(["reglages_sauvegarde"], journal.Actions);
    }
}
