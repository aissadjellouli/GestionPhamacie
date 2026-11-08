using Pharma.Application.Achats;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;
using Fournisseur = Pharma.Application.Achats.Fournisseur;

namespace Pharma.Application.Tests;

/// <summary>Achats en mémoire : seules les méthodes utilisées par les cas d'usage testés sont implémentées.</summary>
internal sealed class AchatsEnMemoire : IAchats
{
    public Dictionary<long, LotRetournable> Lots { get; } = [];
    public NouveauRetour? RetourCree { get; private set; }
    public NouvelleReception? ReceptionRecue { get; private set; }
    public List<(long Commande, long Produit, int Qte, decimal Prix)> LignesAjoutees { get; } = [];
    public DernierAchat? Dernier { get; set; }
    public CommandeDetail? Commande { get; set; }

    public Task<Fournisseur?> FournisseurAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<Fournisseur?>(id is 1 or 2 ? new Fournisseur(id, $"Grossiste {id}", null, 0, null) : null);
    public Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult<string?>(produitId == 99 ? null : $"Produit {produitId}");
    public Task<DernierAchat?> DernierAchatAsync(long produitId, long fournisseurId, CancellationToken ct = default) => Task.FromResult(Dernier);
    public Task<long> BrouillonAsync(long fournisseurId, long utilisateurId, CancellationToken ct = default) => Task.FromResult(10L + fournisseurId);
    public Task AjouterLigneAsync(long commandeId, long produitId, int quantite, decimal prixHt, CancellationToken ct = default)
    {
        LignesAjoutees.Add((commandeId, produitId, quantite, prixHt));
        return Task.CompletedTask;
    }
    public Task<CommandeDetail?> CommandeAsync(long id, CancellationToken ct = default) => Task.FromResult(Commande?.Entete.Id == id ? Commande : null);
    public Task<IReadOnlyDictionary<long, decimal>> TauxTvaAsync(IEnumerable<long> produitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, decimal>>(produitIds.Where(p => p != 99).ToDictionary(p => p, p => p == 2 ? 19m : 0m));
    public Task<ReceptionEnregistree> ReceptionnerAsync(NouvelleReception reception, CancellationToken ct = default)
    {
        ReceptionRecue = reception;
        return Task.FromResult(new ReceptionEnregistree(1, reception.MontantTtc, []));
    }
    public Task<LotRetournable?> LotRetournableAsync(long lotId, CancellationToken ct = default) => Task.FromResult(Lots.GetValueOrDefault(lotId));
    public Task<long> CreerRetourAsync(NouveauRetour retour, CancellationToken ct = default)
    {
        RetourCree = retour;
        return Task.FromResult(7L);
    }

    public Task<IReadOnlyList<Fournisseur>> FournisseursAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<CommandeResume>> CommandesAsync(int limite, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<SuggestionCommande>> SuggestionsAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(long Id, string Nom)?> ProduitParGtinAsync(string gtin, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ModifierLigneAsync(long commandeId, long ligneId, int quantite, decimal prixHt, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SupprimerLigneAsync(long commandeId, long ligneId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task ChangerStatutCommandeAsync(long commandeId, string statut, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<ReceptionResume>> ReceptionsAsync(int limite, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<long>> LotsEnStockAsync(long produitId, string? numeroLot, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RetourResume?> RetourAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<RetourResume>> RetoursAsync(int limite, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MarquerEnvoyeAsync(long retourId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task EnregistrerAvoirAsync(long retourId, decimal montant, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RefuserRetourAsync(long retourId, long utilisateurId, string commentaire, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LotAEtiqueter>> LotsAEtiqueterAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<EtiquetteBoite>> CreerEtiquettesAsync(long lotId, int nombre, CancellationToken ct = default) => throw new NotSupportedException();
}

public class AchatsApplicationTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    /// <summary>Lot de 10 boîtes, dont 3 identifiées (étiquettes E1 à E3) : 7 sans identité.</summary>
    private static LotRetournable Lot(long id = 1, long fournisseur = 1) =>
        new(id, 4, "Metformine", $"L{id}", Aujourdhui.AddMonths(2), fournisseur, $"Grossiste {fournisseur}", 10, 150m, false,
            [new BoiteEnStock(101, "E1"), new BoiteEnStock(102, "E2"), new BoiteEnStock(103, "E3")]);

    private static PreparerRetour Retour(AchatsEnMemoire achats, params string[] droits) =>
        new(achats, new DroitsFactices(droits.Length == 0 ? ["achats.retour_fournisseur"] : droits), new JournalEspion());

    [Fact]
    public async Task Retour_des_boites_sans_identite_sans_scan()
    {
        var achats = new AchatsEnMemoire { Lots = { [1] = Lot() } };
        await Retour(achats).ExecuterAsync(MotifRetour.PeremptionProche, [new LotARetourner(1, 7, [])], 5);
        Assert.Equal(1050m, achats.RetourCree!.MontantAttendu);   // 7 × 150
    }

    [Fact]
    public async Task Au_dela_des_boites_sans_identite_il_faut_scanner()
    {
        var achats = new AchatsEnMemoire { Lots = { [1] = Lot() } };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Retour(achats).ExecuterAsync(MotifRetour.Defaut, [new LotARetourner(1, 9, ["E1"])], 5));
        Assert.Contains("au moins 2", ex.Message);

        await Retour(achats).ExecuterAsync(MotifRetour.Defaut, [new LotARetourner(1, 9, ["E1", " E3 "])], 5);
        Assert.Equal([101L, 103L], achats.RetourCree!.Lignes.Single().BoiteIds);
    }

    [Fact]
    public async Task Boite_d_un_autre_lot_refusee()
    {
        var achats = new AchatsEnMemoire { Lots = { [1] = Lot() } };
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Retour(achats).ExecuterAsync(MotifRetour.Defaut, [new LotARetourner(1, 1, ["AUTRE"])], 5));
    }

    [Fact]
    public async Task Un_retour_par_grossiste()
    {
        var achats = new AchatsEnMemoire { Lots = { [1] = Lot(1, 1), [2] = Lot(2, 2) } };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Retour(achats).ExecuterAsync(MotifRetour.RappelLot,
            [new LotARetourner(1, 1, []), new LotARetourner(2, 1, [])], 5));
        Assert.Contains("grossistes différents", ex.Message);
    }

    [Fact]
    public async Task Retour_sans_droit_refuse() =>
        await Assert.ThrowsAsync<Pharma.Application.Commun.DroitRefuseException>(() =>
            Retour(new AchatsEnMemoire { Lots = { [1] = Lot() } }, "caisse.vendre").ExecuterAsync(MotifRetour.Defaut, [new LotARetourner(1, 1, [])], 5));

    [Fact]
    public async Task Commande_reprend_le_dernier_prix_d_achat()
    {
        var achats = new AchatsEnMemoire { Dernier = new DernierAchat(160m, 2) };
        var id = await new AjouterACommande(achats, new DroitsFactices("achats.commander")).ExecuterAsync(1, 4, 60, null, 5);
        Assert.Equal(11, id);
        Assert.Equal((11L, 4L, 60, 160m), achats.LignesAjoutees.Single());
    }

    [Fact]
    public async Task Sans_achat_precedent_le_prix_est_demande() =>
        Assert.Contains("prix", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AjouterACommande(new AchatsEnMemoire(), new DroitsFactices("achats.commander")).ExecuterAsync(1, 4, 5, null, 5))).Message);

    private static Receptionner Reception(AchatsEnMemoire achats) =>
        new(achats, new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion());

    [Fact]
    public async Task Reception_montant_ttc_avec_la_tva_du_catalogue()
    {
        var achats = new AchatsEnMemoire();
        await Reception(achats).ExecuterAsync(new SaisieReception(1, null, " BL-9 ", null,
        [
            new LigneReceptionSaisie(1, "saisi", " L1 ", Aujourdhui.AddYears(1), 10, 100m),   // TVA 0 %
            new LigneReceptionSaisie(2, "saisi", "L2", Aujourdhui.AddYears(1), 2, 50m)        // TVA 19 %
        ]), 5);
        var r = achats.ReceptionRecue!;
        Assert.Equal(1119m, r.MontantTtc);
        Assert.Equal("BL-9", r.NumeroBl);
        Assert.Equal("L1", r.Lignes[0].NumeroLot);
        Assert.Equal("Produit 1", r.Lignes[0].Nom);   // nom du catalogue
    }

    [Fact]
    public async Task Reception_sur_la_commande_d_un_autre_grossiste_refusee()
    {
        var achats = new AchatsEnMemoire
        {
            Commande = new CommandeDetail(new CommandeResume(3, "C-000003", 2, "Grossiste 2", DateTime.UtcNow, "envoyee", 1, 100), [])
        };
        await Assert.ThrowsAsync<RegleMetierException>(() => Reception(achats).ExecuterAsync(new SaisieReception(1, 3, "BL", null,
            [new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 1, 1m)]), 5));
    }

    [Fact]
    public async Task Meme_lot_sur_deux_lignes_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => Reception(new AchatsEnMemoire()).ExecuterAsync(new SaisieReception(1, null, "BL", null,
            [new LigneReceptionSaisie(1, "x", "l1", Aujourdhui.AddYears(1), 1, 1m), new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 1, 1m)]), 5));
}
