using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class AttenteEnMemoire : IVentesEnAttente
{
    public List<(long Id, string Poste, string Contenu)> Paniers { get; } = [];
    public Task<IReadOnlyList<VenteEnAttente>> ListerAsync(string poste, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VenteEnAttente>>(Paniers.Where(p => p.Poste == poste)
            .Select(p => new VenteEnAttente(p.Id, "x", 1, 0, "E", DateTime.UtcNow)).ToList());
    public Task<long> EnregistrerAsync(string poste, long u, string l, int n, decimal t, string c, CancellationToken ct = default)
    {
        var id = Paniers.Count + 1L;
        Paniers.Add((id, poste, c));
        return Task.FromResult(id);
    }
    public Task<string?> ReprendreAsync(long id, string poste, CancellationToken ct = default)
    {
        var p = Paniers.FirstOrDefault(x => x.Id == id && x.Poste == poste);
        if (p.Contenu is null) return Task.FromResult<string?>(null);
        Paniers.Remove(p);
        return Task.FromResult<string?>(p.Contenu);
    }
    public Task<bool> SupprimerAsync(long id, string poste, CancellationToken ct = default) =>
        Task.FromResult(Paniers.RemoveAll(x => x.Id == id && x.Poste == poste) > 0);
}

public class VentesEnAttenteTests
{
    [Fact]
    public async Task Mise_en_attente_puis_reprise_une_seule_fois()
    {
        var gerer = new GererVentesEnAttente(new AttenteEnMemoire());
        var id = await gerer.MettreEnAttenteAsync("Caisse 1", 5, "Patient fictif", 2, 470, "{\"a\":1}");
        Assert.Equal("{\"a\":1}", await gerer.ReprendreAsync(id, "Caisse 1"));
        // Un collègue qui clique en même temps ne la récupère pas une seconde fois
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.ReprendreAsync(id, "Caisse 1"));
    }

    [Fact]
    public async Task Un_autre_poste_ne_voit_pas_les_ventes_en_attente()
    {
        var gerer = new GererVentesEnAttente(new AttenteEnMemoire());
        var id = await gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 120, "{}");
        Assert.Empty(await gerer.ListerAsync("Caisse 2"));
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.ReprendreAsync(id, "Caisse 2"));
    }

    [Fact]
    public async Task Panier_vide_poste_inconnu_ou_trop_de_ventes_refuses()
    {
        var stock = new AttenteEnMemoire();
        var gerer = new GererVentesEnAttente(stock);
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 0, 0, "{}"));
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync(" ", 5, null, 1, 0, "{}"));
        for (var i = 0; i < GererVentesEnAttente.MaxParPoste; i++) await gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}");
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}"));
    }
}
