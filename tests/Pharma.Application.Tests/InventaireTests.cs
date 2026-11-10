using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

internal sealed class InventairesEnMemoire : IInventaires
{
    public InventaireDetail Detail { get; set; } = new(5, "C", "en_cours", DateTime.UtcNow, "Assistant", null,
    [
        Ligne(1, 101, "Metformine", compte: 30, theorique: 30, prix: 150),     // conforme
        Ligne(2, 102, "Amlodipine", compte: 6, theorique: 8, prix: 200),       // -2 boîtes, -400 DA : corrigé
        Ligne(3, 103, "Sintrom", compte: 20, theorique: 25, prix: 200)         // -5 boîtes : à valider
    ]);
    public IReadOnlyList<DecisionLigne>? Decisions { get; private set; }
    public List<(long Ligne, int Compte)> Comptages { get; } = [];
    public List<(long Ligne, bool Valider)> Decides { get; } = [];
    public long? LotScanne { get; set; } = 102;

    public static LigneInventaire Ligne(long id, long lot, string produit, int? compte, int? theorique, decimal prix) =>
        new(id, lot, produit, $"L{lot}", new DateOnly(2027, 6, 30), false, "C-01-1", prix, compte, null, compte is null ? "a_compter" : "compte", theorique, null, null);

    public Task<ParametresInventaire> ParametresAsync(CancellationToken ct = default) => Task.FromResult(new ParametresInventaire(2, 2000));
    public Task ModifierParametresAsync(ParametresInventaire p, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<RayonInventaire>> RayonsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RayonInventaire>>([new("C", null, 3, 0, null), new("V", null, 0, 0, null)]);
    public Task<long> CreerAsync(string rayon, long u, CancellationToken ct = default) => Task.FromResult(5L);
    public Task<InventaireDetail?> DetailAsync(long id, CancellationToken ct = default) => Task.FromResult<InventaireDetail?>(id == Detail.Id ? Detail : null);
    public Task EnregistrerComptageAsync(long ligneId, int compte, DateOnly? d, long u, CancellationToken ct = default) { Comptages.Add((ligneId, compte)); return Task.CompletedTask; }
    public Task<long?> LotParCodeAsync(string code, CancellationToken ct = default) => Task.FromResult(LotScanne);
    public Task TerminerAsync(long id, IReadOnlyList<DecisionLigne> decisions, long u, CancellationToken ct = default) { Decisions = decisions; return Task.CompletedTask; }
    public Task AnnulerAsync(long id, CancellationToken ct = default) => Task.CompletedTask;
    public Task<IReadOnlyList<EcartAValider>> EcartsAValiderAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EcartAValider>>([new(3, 5, "C", "Sintrom", "L103", 25, 20, -5, -1000, "Assistant", DateTime.UtcNow)]);
    public Task DeciderAsync(long ligneId, bool valider, long u, CancellationToken ct = default) { Decides.Add((ligneId, valider)); return Task.CompletedTask; }
    public Task<IReadOnlyList<InventaireResume>> HistoriqueAsync(int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<InventaireResume>>([]);
}

public class InventaireApplicationTests
{
    [Fact]
    public async Task Comptage_a_l_aveugle_pendant_l_inventaire()
    {
        var d = await new ObtenirInventaire(new InventairesEnMemoire()).ExecuterAsync(5);
        Assert.All(d!.Lignes, l => { Assert.Null(l.Theorique); Assert.Null(l.Ecart); });
    }

    [Fact]
    public async Task Terminer_corrige_les_petits_ecarts_et_previent_le_titulaire_pour_les_gros()
    {
        var (depot, alertes) = (new InventairesEnMemoire(), new AlertesTitulaireEspion());
        var bilan = await new TerminerInventaire(depot, new DroitsFactices("stock.ajuster"), new JournalEspion(), alertes).ExecuterAsync(5, 2);

        Assert.Equal(new BilanInventaire(3, 1, 1, 1, -7, -1400), bilan);
        Assert.Equal([DecisionEcart.Conforme, DecisionEcart.Ajuste, DecisionEcart.AValider], depot.Decisions!.Select(d => d.Decision));
        Assert.Equal(("C", 1), alertes.Inventaires.Single());
    }

    [Fact]
    public async Task Terminer_refuse_s_il_reste_des_lots_a_compter()
    {
        var depot = new InventairesEnMemoire();
        depot.Detail = depot.Detail with { Lignes = [.. depot.Detail.Lignes, InventairesEnMemoire.Ligne(4, 104, "Oubli", null, null, 10)] };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new TerminerInventaire(depot, new DroitsFactices("stock.ajuster"), new JournalEspion()).ExecuterAsync(5, 2));
        Assert.Contains("1 lot(s)", ex.Message);
    }

    [Fact]
    public async Task Scan_ajoute_une_boite_au_lot_et_refuse_un_lot_d_un_autre_rayon()
    {
        var depot = new InventairesEnMemoire();
        var cas = new ScannerBoiteInventaire(depot, new DroitsFactices("stock.ajuster"));
        var r = await cas.ExecuterAsync(5, "2000000000077", 2);
        Assert.Equal((2L, 7), (r.LigneId, r.Compte));
        depot.LotScanne = 999;
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(5, "2000000000084", 2));
    }

    [Fact]
    public async Task Seul_le_titulaire_valide_un_ecart()
    {
        var depot = new InventairesEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new DeciderEcartInventaire(depot, new DroitsFactices("stock.ajuster", "admin.parametres"), new JournalEspion()).ExecuterAsync(3, true, 2));
        await new DeciderEcartInventaire(depot, new DroitsFactices { Titulaire = true }, new JournalEspion()).ExecuterAsync(3, true, 1);
        Assert.Equal([(3L, true)], depot.Decides);
    }

    [Fact]
    public async Task Rayon_vide_ou_sans_droit_refuse()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CommencerInventaire(new InventairesEnMemoire(), new DroitsFactices("stock.ajuster")).ExecuterAsync("V", 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CommencerInventaire(new InventairesEnMemoire(), new DroitsFactices("stock.consulter")).ExecuterAsync("C", 3));
    }
}
