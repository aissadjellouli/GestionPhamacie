using Pharma.Application.Commun;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class RisquesEnMemoire : IRisques
{
    public (DateOnly Du, DateOnly Au)? Periode { get; private set; }
    public List<JourEcarts> Jours { get; } = [];
    public List<ActiviteEmploye> Employes { get; } = [];
    public Task<IReadOnlyList<JourEcarts>> JoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        Periode = (du, au);
        return Task.FromResult<IReadOnlyList<JourEcarts>>(Jours);
    }
    public Task<IReadOnlyList<ActiviteEmploye>> EmployesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ActiviteEmploye>>(Employes);
}

public class RisquesTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 30));
    private static ActiviteEmploye Employe(long id, string nom) => new(id, nom, true, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    private static JourEcarts Jour(int j, decimal manque, decimal stock, params long[] presents) =>
        new(new DateOnly(2026, 11, j), -manque, manque, stock, 0, 0, 0, 0, presents, ["Caisse 1"]);

    [Fact]
    public async Task Reserve_au_titulaire_et_periode_controlee()
    {
        var depot = new RisquesEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ObtenirTableauRisques(depot, new DroitsFactices("admin.parametres"), Horloge)
            .ExecuterAsync(null, null, 2));
        var cas = new ObtenirTableauRisques(depot, new DroitsFactices { Titulaire = true }, Horloge);
        await cas.ExecuterAsync(null, null, 1);
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), depot.Periode);      // 30 derniers jours par défaut
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(new DateOnly(2026, 11, 30), new DateOnly(2026, 11, 1), 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 30), 1));
    }

    [Fact]
    public async Task Employe_signale_en_tete_perte_du_jour_caisse_et_stock()
    {
        var depot = new RisquesEnMemoire();
        depot.Employes.AddRange([Employe(3, "Assistant"), Employe(7, "Vendeur")]);
        depot.Jours.AddRange([Jour(1, 300, 200, 3, 7), Jour(2, 0, 0, 3), Jour(3, 400, 0, 3, 7), Jour(4, 0, 0, 3), Jour(5, 0, 250, 3, 7), Jour(6, 0, 0, 3)]);
        var t = await new ObtenirTableauRisques(depot, new DroitsFactices { Titulaire = true }, Horloge).ExecuterAsync(null, null, 1);
        var premier = t.Employes[0];
        Assert.Equal(("Vendeur", true, 1150m), (premier.Activite.Nom, premier.Presence.Signale, premier.Presence.PerteEnPresence));
        Assert.False(t.Employes[1].Presence.Signale);
        Assert.Equal(500m, depot.Jours[0].Perte);
    }
}
