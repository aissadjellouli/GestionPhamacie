using Pharma.Application.Achats;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Entrées de stock en mémoire : un lot, son journal de corrections et ses mouvements.</summary>
internal sealed class EntreesStockEnMemoire : IEntreesStock
{
    public EntreeStock Lot { get; set; } = new(7, 1, "Amlor 5 mg", "AM55", new DateOnly(2027, 3, 31), 10, 197.79m, 2.50m, 2, "Grossiste 2",
        new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), false, 0);
    /// <summary>Autres lots (produit, grossiste, n°, péremption) : contrainte ux_lot_fournisseur_date.</summary>
    public List<(long Produit, long Fournisseur, string Numero, DateOnly Peremption)> Autres { get; } = [];
    public List<CorrectionLot> Journal { get; } = [];
    public List<int> Mouvements { get; } = [];

    public Task<Page<EntreeStock>> PageAsync(FiltreEntreesStock filtre, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new Page<EntreeStock>([Lot], 1, 1, page.Taille));
    public Task<EntreeStock?> LotAsync(long lotId, CancellationToken ct = default) => Task.FromResult(lotId == Lot.LotId ? Lot : null);
    public Task<bool> LotIdentiqueExisteAsync(long lotId, long produitId, long fournisseurId, string numeroLot, DateOnly peremption, CancellationToken ct = default) =>
        Task.FromResult(Autres.Contains((produitId, fournisseurId, numeroLot, peremption)));
    public Task<IReadOnlyList<CorrectionLot>> CorrectionsAsync(long lotId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CorrectionLot>>(Journal);

    public List<(long Produit, string Numero)> Rappeles { get; } = [];
    public CorrectionAEcrire? Ecrite { get; private set; }
    public Task<bool> NumeroRappeleAsync(long lotId, long produitId, string numeroLot, CancellationToken ct = default) =>
        Task.FromResult(Rappeles.Contains((produitId, numeroLot)));

    public Task CorrigerAsync(CorrectionAEcrire c, CancellationToken ct = default)
    {
        Ecrite = c;
        if (c.QuantiteAvant != Lot.Quantite) throw new RegleMetierException("Le stock de ce lot vient de changer.");
        if (c.Quantite is { } q) Mouvements.Add(q - c.QuantiteAvant);   // ajustement : écart signé
        foreach (var champ in c.Champs)
            Journal.Add(new CorrectionLot(Journal.Count + 1, champ.Champ, champ.AncienneValeur, champ.NouvelleValeur, c.Motif, DateTimeOffset.UtcNow, "Employé"));
        Lot = Lot with
        {
            Ppa = c.Ppa ?? Lot.Ppa, NumeroLot = c.NumeroLot ?? Lot.NumeroLot, DatePeremption = c.DatePeremption ?? Lot.DatePeremption,
            Shp = c.Shp ?? Lot.Shp, Quantite = c.Quantite ?? Lot.Quantite, Corrections = Lot.Corrections + c.Champs.Count
        };
        return Task.CompletedTask;
    }
}

/// <summary>Lot 4 du modèle Pharma (2026-11-15) : SHP par lot, péremption MM/AA, correction tracée d'une entrée de stock, date de facture.</summary>
public class StockShpCorrectionFactureTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);

    private static (CorrigerEntreeStock Cas, EntreesStockEnMemoire Depot, JournalEspion Journal) Cas(params string[] droits)
    {
        var depot = new EntreesStockEnMemoire();
        var journal = new JournalEspion();
        return (new CorrigerEntreeStock(depot, new DroitsFactices(droits)), depot, journal);
    }

    private static DemandeCorrectionLot Demande(decimal? ppa = null, string? numero = null, DateOnly? peremption = null, decimal? shp = null,
        int? quantite = null, string? motif = "Erreur de saisie") => new(ppa, numero, peremption, shp, quantite, motif);

    // ---- Péremption MM/AA ----

    [Theory]
    [InlineData("02/28", 2028, 2, 29)]   // année bissextile
    [InlineData("02/27", 2027, 2, 28)]
    [InlineData("12/26", 2026, 12, 31)]
    [InlineData("4/2029", 2029, 4, 30)]
    [InlineData("09-30", 2030, 9, 30)]
    [InlineData("15/03/2027", 2027, 3, 15)] // JJ/MM/AAAA toujours accepté
    [InlineData("2027-03-15", 2027, 3, 15)]
    public void Peremption_MM_AA_devient_le_dernier_jour_du_mois(string saisie, int a, int m, int j) =>
        Assert.Equal(new DateOnly(a, m, j), LecturePeremption.Lire(saisie));

    [Theory]
    [InlineData("13/27")]
    [InlineData("00/27")]
    [InlineData("31/02/2027")]
    [InlineData("ab/cd")]
    [InlineData("2027")]
    [InlineData("")]
    public void Peremption_illisible_refusee(string saisie) => Assert.Null(LecturePeremption.Lire(saisie));

    // ---- Date de facture (D-6) ----

    [Fact]
    public void Date_de_facture_ni_dans_le_futur_ni_plus_de_90_jours_avant_la_livraison()
    {
        RegleFactureAchat.VerifierDateFacture(null, Aujourdhui);
        RegleFactureAchat.VerifierDateFacture(Aujourdhui, Aujourdhui);
        RegleFactureAchat.VerifierDateFacture(Aujourdhui.AddDays(-90), Aujourdhui);
        Assert.Equal("date_facture", Assert.Throws<RegleMetierException>(() => RegleFactureAchat.VerifierDateFacture(Aujourdhui.AddDays(1), Aujourdhui)).Champ);
        Assert.Equal("date_facture", Assert.Throws<RegleMetierException>(() => RegleFactureAchat.VerifierDateFacture(Aujourdhui.AddDays(-91), Aujourdhui)).Champ);
    }

    [Fact]
    public async Task Reception_garde_la_date_de_facture_et_le_shp_de_la_ligne()
    {
        var achats = new AchatsEnMemoire();
        var cas = new Receptionner(achats, new DroitsFactices("achats.receptionner", "prix.modifier"), new HorlogeFixe(Aujourdhui), new JournalEspion());
        var ligne = new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 4, 100m);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(new SaisieReception(1, null, "BL-1", "F-1", [ligne], DateFacture: Aujourdhui.AddDays(1)), 5));
        Assert.Null(achats.ReceptionRecue);

        await cas.ExecuterAsync(new SaisieReception(1, null, "BL-1", "F-1", [ligne], DateFacture: Aujourdhui.AddDays(-3)), 5);
        Assert.Equal(Aujourdhui.AddDays(-3), achats.ReceptionRecue!.DateFacture);
        Assert.NotNull(achats.ReceptionRecue.Lignes[0].Shp);   // SHP de la ligne (liste du produit si non saisi) : écrit sur le lot
    }

    // ---- Correction d'une entrée de stock ----

    [Fact]
    public async Task Correction_refusee_sans_le_droit()
    {
        var (cas, depot, _) = Cas("stock.ajuster", "stock.consulter");
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(7, Demande(ppa: 200m), 5));
        Assert.Equal("stock.corriger_entree", ex.Permission);
        Assert.Empty(depot.Journal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public async Task Correction_sans_motif_refusee(string? motif)
    {
        var (cas, depot, _) = Cas("stock.corriger_entree");
        Assert.Equal("motif", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(7, Demande(ppa: 200m, motif: motif), 5))).Champ);
        Assert.Empty(depot.Journal);
    }

    [Fact]
    public async Task Correction_sans_changement_refusee()
    {
        var (cas, _, _) = Cas("stock.corriger_entree");
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(7, Demande(ppa: 197.79m, numero: "am55", shp: 2.50m, quantite: 10), 5));
    }

    [Fact]
    public async Task Correction_inscrit_ancienne_et_nouvelle_valeur_par_champ()
    {
        var (cas, depot, journal) = Cas("stock.corriger_entree");
        var lot = await cas.ExecuterAsync(7, Demande(ppa: 210m, peremption: new DateOnly(2027, 4, 30), shp: 1.50m), 5);

        Assert.Equal((210m, new DateOnly(2027, 4, 30), 1.50m), (lot.Ppa, lot.DatePeremption, lot.Shp));
        Assert.Equal(3, depot.Journal.Count);
        Assert.Contains(depot.Journal, c => c is { Champ: "ppa", AncienneValeur: "197.79", NouvelleValeur: "210.00", Motif: "Erreur de saisie" });
        Assert.Contains(depot.Journal, c => c is { Champ: "date_peremption", AncienneValeur: "31/03/2027", NouvelleValeur: "30/04/2027" });
        Assert.Contains(depot.Journal, c => c is { Champ: "shp", AncienneValeur: "2.50", NouvelleValeur: "1.50" });
        Assert.Empty(depot.Mouvements);   // sans quantité, aucun mouvement de stock
        Assert.Equal("correction_entree_stock", depot.Ecrite!.Journal!.Action);   // écrit dans la transaction de la correction
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Correction_de_quantite_passe_par_un_mouvement_de_stock()
    {
        var (cas, depot, _) = Cas("stock.corriger_entree");
        await cas.ExecuterAsync(7, Demande(quantite: 7), 5);
        Assert.Equal([-3], depot.Mouvements);
        Assert.Contains(depot.Journal, c => c is { Champ: "quantite", AncienneValeur: "10", NouvelleValeur: "7" });
        Assert.Equal(7, depot.Lot.Quantite);

        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(7, Demande(quantite: -1), 5));
    }

    [Fact]
    public async Task Quantite_sous_les_boites_etiquetees_en_stock_refusee()
    {
        var (cas, depot, _) = Cas("stock.corriger_entree");
        depot.Lot = depot.Lot with { BoitesEnStock = 8 };
        Assert.Equal("quantite", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(7, Demande(quantite: 5), 5))).Champ);
        Assert.Empty(depot.Mouvements);
    }

    [Fact]
    public async Task Nouveau_numero_de_lot_deja_pris_pour_ce_produit_et_ce_grossiste_refuse_clairement()
    {
        var (cas, depot, _) = Cas("stock.corriger_entree");
        depot.Autres.Add((1, 2, "AM56", new DateOnly(2027, 3, 31)));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(7, Demande(numero: " am56 "), 5));
        Assert.Equal("numero_lot", ex.Champ);
        Assert.Contains("existe déjà", ex.Message);
        Assert.Empty(depot.Journal);

        // Autre péremption : lot distinct, correction acceptée (n° normalisé en majuscules)
        var lot = await cas.ExecuterAsync(7, Demande(numero: "am56", peremption: new DateOnly(2027, 5, 31)), 5);
        Assert.Equal("AM56", lot.NumeroLot);
        Assert.Contains(depot.Journal, c => c is { Champ: "numero_lot", AncienneValeur: "AM55", NouvelleValeur: "AM56" });
    }

    [Fact]
    public async Task Lot_introuvable_refuse()
    {
        var (cas, _, _) = Cas("stock.corriger_entree");
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(99, Demande(ppa: 1m), 5));
    }
}
