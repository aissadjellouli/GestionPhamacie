using Pharma.Application.Commun;
using Pharma.Application.Rapports;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Accueil (2026-11-15) : ventes du jour comparées au même jour de la semaine précédente, arrêté à la même heure.</summary>
public class VentesDuJourTests
{
    private sealed class HorlogeFixe(DateTime utc) : IHorloge
    {
        public DateTime MaintenantUtc => utc;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(utc.ToLocalTime());
    }

    private static readonly DateTime Maintenant = new(2026, 11, 15, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task J_moins_7_est_arrete_a_la_meme_heure()
    {
        var rapports = new RapportsEnMemoire();
        var v = await new ObtenirVentesDuJour(rapports, new DroitsFactices("rapports.ventes", "caisse.voir_ecart"), new HorlogeFixe(Maintenant)).ExecuterAsync(1);

        var local = Maintenant.ToLocalTime();
        Assert.Equal(DateOnly.FromDateTime(local).AddDays(-7), v.JourReference);
        Assert.Equal((DateOnly.FromDateTime(local).AddDays(-7), local.TimeOfDay), rapports.DemandeHeure);
        Assert.Equal(12, v.Aujourdhui.NombreVentes);
        Assert.Equal(10, v.Reference.NombreVentes);
        Assert.Null(v.Marge);
    }

    [Fact]
    public async Task Sans_le_droit_de_voir_les_ecarts_les_ventes_du_jour_sont_refusees()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ObtenirVentesDuJour(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes"), new HorlogeFixe(Maintenant)).ExecuterAsync(1));
    }

    [Fact]
    public async Task La_marge_n_est_donnee_qu_avec_le_droit_des_marges()
    {
        var v = await new ObtenirVentesDuJour(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes", "caisse.voir_ecart", "rapports.marges"),
            new HorlogeFixe(Maintenant)).ExecuterAsync(1);
        Assert.NotNull(v.Marge);
    }
}
