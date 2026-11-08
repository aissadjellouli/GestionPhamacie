using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

internal sealed class EmplacementsEnMemoire : IEmplacements
{
    public List<Emplacement> Liste { get; } =
    [
        new(1, "A-01-1", "A", "01", "1", "Antalgiques", ZoneEmplacement.Vente, true, 1),
        new(2, "R-01", "R", "01", null, "Réserve", ZoneEmplacement.Reserve, true, 0),
        new(3, "Z-09", "Z", "09", null, null, ZoneEmplacement.Vente, false, 0)
    ];
    public string? CodeCree { get; private set; }
    public (long Produit, long? Vente, long? Reserve)? Range { get; private set; }

    public Task<IReadOnlyList<Emplacement>> ListerAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Emplacement>>(Liste);
    public Task<bool> CodeExisteAsync(string code, long? saufId, CancellationToken ct = default) =>
        Task.FromResult(Liste.Any(e => e.Code == code && e.Id != saufId));
    public Task<long> CreerAsync(string code, string r, string? e, string? t, string? l, ZoneEmplacement z, CancellationToken ct = default)
    {
        CodeCree = code;
        return Task.FromResult(9L);
    }
    public Task<bool> ModifierAsync(long id, string code, string r, string? e, string? t, string? l, ZoneEmplacement z, bool a, CancellationToken ct = default) =>
        Task.FromResult(Liste.Any(x => x.Id == id));
    public Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default) => Task.FromResult<string?>(produitId == 1 ? "Paracétamol" : null);
    public Task RangerAsync(long produitId, long? v, long? r, CancellationToken ct = default) { Range = (produitId, v, r); return Task.CompletedTask; }
}

public class EmplacementsApplicationTests
{
    [Fact]
    public async Task Creation_avec_code_compose_et_journal()
    {
        var (depot, journal) = (new EmplacementsEnMemoire(), new JournalEspion());
        await new EnregistrerEmplacement(depot, new DroitsFactices("admin.parametres"), journal)
            .ExecuterAsync(null, new SaisieEmplacement("c", "03", "1", " Anticoagulants ", ZoneEmplacement.Vente, true), 1);
        Assert.Equal("C-03-1", depot.CodeCree);
        Assert.Equal(["emplacement"], journal.Actions);
    }

    [Fact]
    public async Task Code_deja_pris_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new EnregistrerEmplacement(new EmplacementsEnMemoire(), new DroitsFactices("admin.parametres"), new JournalEspion())
            .ExecuterAsync(null, new SaisieEmplacement("A", "01", "1", null, ZoneEmplacement.Vente, true), 1));

    [Fact]
    public async Task Gestion_des_emplacements_sans_droit_refusee() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() => new EnregistrerEmplacement(new EmplacementsEnMemoire(), new DroitsFactices("stock.consulter"), new JournalEspion())
            .ExecuterAsync(null, new SaisieEmplacement("B", null, null, null, ZoneEmplacement.Vente, true), 1));

    [Fact]
    public async Task Rangement_d_un_produit()
    {
        var depot = new EmplacementsEnMemoire();
        await new RangerProduit(depot, new DroitsFactices("stock.ajuster"), new JournalEspion()).ExecuterAsync(1, 1, 2, 5);
        Assert.Equal((1L, 1L, 2L), depot.Range);
    }

    [Fact]
    public async Task Rangement_refuse_si_zone_fausse_ou_emplacement_desactive()
    {
        var cas = new RangerProduit(new EmplacementsEnMemoire(), new DroitsFactices("stock.ajuster"), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, 2, null, 5));   // réserve donnée comme vente
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, 3, null, 5));   // désactivé
    }

    [Fact]
    public async Task Rangement_sans_droit_refuse() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new RangerProduit(new EmplacementsEnMemoire(), new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync(1, 1, null, 5));
}
