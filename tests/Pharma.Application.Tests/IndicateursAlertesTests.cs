using Pharma.Application.Alertes;
using Pharma.Application.Suivi;

namespace Pharma.Application.Tests;

internal sealed class HorizonsEnMemoire : IIndicateursAlertes
{
    public IReadOnlyList<int>? Demandes { get; private set; }

    public Task<IReadOnlyList<HorizonPeremption>> HorizonsAsync(DateOnly aujourdhui, IReadOnlyList<int> jours, CancellationToken ct = default)
    {
        Demandes = jours;
        return Task.FromResult<IReadOnlyList<HorizonPeremption>>(jours.Select(j => new HorizonPeremption(j, j / 30, j, j * 10m)).ToList());
    }
}

internal sealed class DettesEnMemoire(params SommeDue[] sommes) : ISuiviDettes
{
    public Task<IReadOnlyList<SommeDue>> SommesDuesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SommeDue>>(sommes);
    public Task<int> DelaiAlerteAsync(CancellationToken ct = default) => Task.FromResult(30);
    public Task ModifierDelaiAlerteAsync(int jours, CancellationToken ct = default) => Task.CompletedTask;
}

public class IndicateursAlertesTests
{
    private static readonly DateOnly Jour = new(2026, 11, 10);

    private static ObtenirIndicateursAlertes Service(params string[] droits)
    {
        var dettes = new DettesEnMemoire(
            new SommeDue("dette", "grossiste", 1, "Grossiste A", "Facture F-1", Jour.AddDays(-45), null, 4000, false),    // en retard
            new SommeDue("dette", "grossiste", 2, "Grossiste B", "Facture F-2", Jour.AddDays(-5), null, 900, false),      // dans le délai
            new SommeDue("dette", "effet", 1, "Grossiste A", "Effet E-1", Jour.AddDays(-20), Jour.AddDays(3), 1500, false), // à payer sous 7 jours
            new SommeDue("creance", "patient", 3, "Patient", "Crédit", Jour.AddDays(-60), null, 500, false));               // créance : non comptée
        return new ObtenirIndicateursAlertes(new HorizonsEnMemoire(), new DroitsFactices(droits), new HorlogeFixe(Jour),
            new ObtenirSuiviDettes(dettes, new DroitsFactices(droits), new HorlogeFixe(Jour)));
    }

    [Fact]
    public async Task Peremption_a_30_60_et_180_jours()
    {
        var i = await Service("stock.consulter").ExecuterAsync(1);
        Assert.Equal([30, 60, 180], i.Peremption.Select(h => h.Jours));
    }

    [Fact]
    public async Task Reglements_a_preparer_reserves_a_qui_regle_les_grossistes()
    {
        var sans = await Service("stock.consulter").ExecuterAsync(1);
        Assert.Null(sans.ReglementsAPreparer);

        var avec = await Service("stock.consulter", "achats.regler").ExecuterAsync(1);
        Assert.Equal(2, avec.ReglementsAPreparer);          // la facture en retard et l'effet proche
        Assert.Equal(5500m, avec.MontantAPreparer);
    }
}
