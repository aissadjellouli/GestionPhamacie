using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

internal sealed class CampagnesEnMemoire : ICampagnesInventaire
{
    public CampagneInventaire? EnCours { get; set; }
    public List<RayonCampagne> Rayons { get; } = [];
    public bool Cloturee { get; private set; }

    public Task<CampagneInventaire?> EnCoursAsync(CancellationToken ct = default) => Task.FromResult(EnCours);
    public Task<long> CommencerAsync(string mode, long u, CancellationToken ct = default)
    {
        EnCours = new CampagneInventaire(9, mode, "en_cours", DateTime.UtcNow, "Titulaire", null, null, null, null, null, null);
        return Task.FromResult(9L);
    }
    public Task<IReadOnlyList<RayonCampagne>> RayonsAsync(long id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RayonCampagne>>(Rayons);
    public Task CloturerAsync(long id, long u, string? c, CancellationToken ct = default) { Cloturee = true; return Task.CompletedTask; }
    public Task AnnulerAsync(long id, CancellationToken ct = default) { EnCours = null; return Task.CompletedTask; }
    public Task<EtatInventaire?> EtatAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<EtatInventaire?>(new EtatInventaire(EnCours! with { Statut = "cloturee" }, [], 0, 0));
    public Task<IReadOnlyList<CampagneInventaire>> HistoriqueAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CampagneInventaire>>([]);
}

public class InventaireCompletTests
{
    private static GererInventaireComplet Cas(CampagnesEnMemoire c, bool titulaire = true) =>
        new(c, titulaire ? new DroitsFactices("role.titulaire") { Titulaire = true } : new DroitsFactices("stock.ajuster"), new JournalEspion());

    [Fact]
    public async Task Lancement_reserve_au_titulaire_avec_un_mode()
    {
        await Assert.ThrowsAsync<Pharma.Application.Commun.DroitRefuseException>(() => Cas(new CampagnesEnMemoire(), titulaire: false).CommencerAsync("ouverte", 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(new CampagnesEnMemoire()).CommencerAsync(null, 1));
        var c = new CampagnesEnMemoire();
        await Cas(c).CommencerAsync("fermee", 1);
        Assert.Equal("fermee", c.EnCours!.Mode);
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(c).CommencerAsync("ouverte", 1));
    }

    [Fact]
    public async Task Cloture_apres_tous_les_rayons_et_tous_les_ecarts_decides()
    {
        var c = new CampagnesEnMemoire();
        await Cas(c).CommencerAsync("ouverte", 1);
        c.Rayons.AddRange([new RayonCampagne("A", 10, 1, "termine", 0), new RayonCampagne("B", 4, null, "a_compter", 0), new RayonCampagne("C", 0, null, "a_compter", 0)]);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Cas(c).CloturerAsync(9, null, 1));
        Assert.Contains("B", e.Message);
        Assert.DoesNotContain("C", e.Message);
        c.Rayons[1] = new RayonCampagne("B", 4, 2, "termine", 1);
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(c).CloturerAsync(9, null, 1));
        c.Rayons[1] = new RayonCampagne("B", 4, 2, "termine", 0);
        await Cas(c).CloturerAsync(9, "RAS", 1);
        Assert.True(c.Cloturee);
    }

    [Fact]
    public async Task Pharmacie_fermee_bloque_les_ventes()
    {
        var c = new CampagnesEnMemoire();
        await VentesSuspendues.VerifierAsync(c, default);
        await Cas(c).CommencerAsync("ouverte", 1);
        await VentesSuspendues.VerifierAsync(c, default);
        c.EnCours = c.EnCours! with { Mode = "fermee" };
        await Assert.ThrowsAsync<RegleMetierException>(() => VentesSuspendues.VerifierAsync(c, default));
    }
}
