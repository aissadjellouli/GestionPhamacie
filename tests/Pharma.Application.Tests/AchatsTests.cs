using Pharma.Application.Commun;
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
    /// <summary>Produits bloqués à l'achat.</summary>
    public HashSet<long> BloquesAchat { get; } = [];
    public Task<IReadOnlyList<InfoAchatProduit>> InfosAchatAsync(IEnumerable<long> produitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<InfoAchatProduit>>(produitIds.Select(id => new InfoAchatProduit(id, id == 2 ? "liste_ii" : "liste_i", 100, 0,
            id == 2 ? Pharma.Domain.Achats.RegleShp.ListeII : Pharma.Domain.Achats.RegleShp.ListeI, BloquesAchat.Contains(id), BloquesAchat.Contains(id) ? "litige" : null)).ToList());
    public Task<IReadOnlyDictionary<long, decimal>> TauxTvaAsync(IEnumerable<long> produitIds, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyDictionary<long, decimal>>(produitIds.Where(p => p != 99).ToDictionary(p => p, p => p == 2 ? 19m : 0m));
    public Task<ReceptionEnregistree> ReceptionnerAsync(NouvelleReception reception, CancellationToken ct = default)
    {
        ReceptionRecue = reception;
        return Task.FromResult(new ReceptionEnregistree(1, reception.MontantTtc,
            reception.Lignes.Select((l, i) => new LotRecu(100 + i, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite, 0)).ToList()));
    }
    public Task<LotRetournable?> LotRetournableAsync(long lotId, CancellationToken ct = default) => Task.FromResult(Lots.GetValueOrDefault(lotId));
    public Task<long> CreerRetourAsync(NouveauRetour retour, CancellationToken ct = default)
    {
        RetourCree = retour;
        return Task.FromResult(7L);
    }

    public Task<IReadOnlyList<Fournisseur>> FournisseursAsync(CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Pharma.Application.Commun.Page<CommandeResume>> CommandesAsync(Pharma.Application.Commun.DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => throw new NotSupportedException();
    public List<SuggestionCommande> Suggestions { get; } = [];
    public Task<IReadOnlyList<SuggestionCommande>> SuggestionsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<SuggestionCommande>>(Suggestions);
    public List<Pharma.Domain.Stock.LotExistant> MemeNumero { get; } = [];
    public Task<IReadOnlyList<Pharma.Domain.Stock.LotExistant>> LotsMemeNumeroAsync(long produitId, long fournisseurId, string numeroLot, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Pharma.Domain.Stock.LotExistant>>(MemeNumero);
    public Dictionary<string, (long Id, string Nom)> Gtins { get; } = [];
    public Task<(long Id, string Nom)?> ProduitParGtinAsync(string gtin, CancellationToken ct = default) =>
        Task.FromResult<(long Id, string Nom)?>(Gtins.TryGetValue(gtin, out var p) ? p : null);
    public Task ModifierLigneAsync(long commandeId, long ligneId, int quantite, decimal prixHt, CancellationToken ct = default) => throw new NotSupportedException();
    public Task SupprimerLigneAsync(long commandeId, long ligneId, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Statut de la commande en base (null : celui de <see cref="Commande"/>), changé par un autre poste dans les tests.</summary>
    public string? StatutEnBase { get; set; }
    public Task<bool> ChangerStatutCommandeAsync(long commandeId, string statutAttendu, string statut, CancellationToken ct = default)
    {
        if ((StatutEnBase ?? Commande?.Entete.Statut) != statutAttendu) return Task.FromResult(false);
        StatutEnBase = statut;
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<ReceptionResume>> ReceptionsAvecDocumentAsync(long f, string n, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ReceptionResume>>(n == "26/F-015695" ? [new ReceptionResume(9, "Qouds", n, null, null, DateTime.UtcNow, 0, 0, 100, f)] : []);
    public Task<Pharma.Application.Commun.Page<ReceptionResume>> ReceptionsAsync(Pharma.Application.Commun.DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<long>> LotsEnStockAsync(long produitId, string? numeroLot, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RetourResume?> RetourAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Pharma.Application.Commun.Page<RetourResume>> RetoursAsync(Pharma.Application.Commun.DemandePage page, FiltreAchats? filtre = null, CancellationToken ct = default) => throw new NotSupportedException();
    public Task MarquerEnvoyeAsync(long retourId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task EnregistrerAvoirAsync(long retourId, decimal montant, CancellationToken ct = default) => throw new NotSupportedException();
    public Task RefuserRetourAsync(long retourId, long utilisateurId, string commentaire, CancellationToken ct = default) => throw new NotSupportedException();
    public List<LotAEtiqueter> AEtiqueter { get; } = [];
    public Task<IReadOnlyList<LotAEtiqueter>> LotsAEtiqueterAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<LotAEtiqueter>>(AEtiqueter);
    public Task<IReadOnlyList<EtiquetteBoite>> CreerEtiquettesAsync(long lotId, int nombre, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<EtiquetteBoite>> EtiquettesReceptionAsync(long receptionId, CancellationToken ct = default) => throw new NotSupportedException();
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
    public async Task Reception_detaillee_remise_timbre_et_SHP_selon_la_liste()
    {
        var achats = new AchatsEnMemoire();
        await Reception(achats).ExecuterAsync(new SaisieReception(1, null, "BL-10", null,
        [
            new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 10, 100m, RemisePct: 10, Ppa: 125m),   // liste I, TVA 0 %
            new LigneReceptionSaisie(2, "x", "L2", Aujourdhui.AddYears(1), 2, 50m, Shp: 0.75m)                     // SHP saisi, TVA 19 %
        ], Timbre: 20m), 5);
        var r = achats.ReceptionRecue!;
        // HT 1 100 ; remise 100 ; net HT 1 000 ; TVA 19 % de 100 = 19 ; timbre 20 → net à payer 1 039 (dette du grossiste)
        Assert.Equal(1039m, r.MontantTtc);
        Assert.Equal((1100m, 100m, 19m, 20m), (r.Totaux!.MontantHt, r.Totaux.Remise, r.Totaux.Tva, r.Totaux.Timbre));
        Assert.Equal(90m, r.Lignes[0].PrixNetHt);                       // coût du stock : net de remise
        Assert.Equal(Pharma.Domain.Achats.RegleShp.ListeI, r.Lignes[0].Shp); // SHP proposé selon la liste
        Assert.Equal(0.75m, r.Lignes[1].Shp);                            // SHP saisi gardé
        Assert.Equal(26.50m, r.Totaux.ShpTotal);                         // 10 × 2,50 + 2 × 0,75, hors net à payer
    }

    [Fact]
    public async Task Remplacer_le_prix_du_catalogue_exige_le_droit_de_modifier_un_prix()
    {
        var achats = new AchatsEnMemoire();
        var ligne = new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 12, 100m, Ppa: 130m, Ug: 2, RemplacerPrixCatalogue: true);
        await Assert.ThrowsAnyAsync<Exception>(() => Reception(achats).ExecuterAsync(new SaisieReception(1, null, "BL-11", null, [ligne]), 5));
        Assert.Null(achats.ReceptionRecue);
        var journal = new JournalEspion();
        await new Receptionner(achats, new DroitsFactices("achats.receptionner", "prix.modifier"), new HorlogeFixe(Aujourdhui), journal)
            .ExecuterAsync(new SaisieReception(1, null, "BL-11", null, [ligne]), 5);
        Assert.Equal(1000m, achats.ReceptionRecue!.MontantTtc);           // 10 boîtes facturées, 2 UG
        Assert.Contains("prix_modifie", journal.Actions);                 // 100 (catalogue de la doublure) → 130
    }

    [Fact]
    public async Task Produit_bloque_a_l_achat_ni_commande_ni_reception()
    {
        var achats = new AchatsEnMemoire();
        achats.BloquesAchat.Add(1);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Reception(achats).ExecuterAsync(new SaisieReception(1, null, "BL-12", null,
            [new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 1, 10m)]), 5));
        Assert.Contains("bloqué à l’achat", ex.Message);
        Assert.Null(achats.ReceptionRecue);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AjouterACommande(achats, new DroitsFactices("achats.commander")).ExecuterAsync(1, 1, 5, 10m, 5));

        // Validée par une personne habilitée : réceptionnée et inscrite au journal
        var journal = new JournalEspion();
        await new Receptionner(achats, new DroitsFactices("achats.receptionner", "produits.bloquer"), new HorlogeFixe(Aujourdhui), journal)
            .ExecuterAsync(new SaisieReception(1, null, "BL-12", null, [new LigneReceptionSaisie(1, "x", "L1", Aujourdhui.AddYears(1), 1, 10m)],
                Blocage: new Pharma.Application.Catalogue.ValidationBlocage(true)), 5);
        Assert.NotNull(achats.ReceptionRecue);
        Assert.Contains("reception_produit_bloque", journal.Actions);
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

    [Fact]
    public async Task Annulation_d_une_commande_emise_entre_temps_sur_un_autre_poste_refusee()
    {
        // Lue « brouillon » à l'écran, mais émise entre-temps : le statut en base ne correspond plus
        var achats = new AchatsEnMemoire
        {
            Commande = new CommandeDetail(new CommandeResume(3, "C-000003", 2, "Grossiste 2", DateTime.UtcNow, "brouillon", 1, 100), []),
            StatutEnBase = "envoyee"
        };
        var journal = new JournalEspion();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new AnnulerCommande(achats, new DroitsFactices("achats.commander"), journal).ExecuterAsync(3, 5));
        Assert.Equal(RegleCommande.ModifieeEntreTemps, e.Message);
        Assert.Empty(journal.Actions);
        Assert.Equal("envoyee", achats.StatutEnBase);
    }

    [Fact]
    public async Task Annulation_avec_le_statut_lu_toujours_en_base_acceptee()
    {
        var achats = new AchatsEnMemoire
        {
            Commande = new CommandeDetail(new CommandeResume(3, "C-000003", 2, "Grossiste 2", DateTime.UtcNow, "envoyee", 1, 100), [])
        };
        var journal = new JournalEspion();
        await new AnnulerCommande(achats, new DroitsFactices("achats.commander"), journal).ExecuterAsync(3, 5);
        Assert.Equal("annulee", achats.StatutEnBase);
        Assert.Equal(["commande_annulee"], journal.Actions);
    }
}

public class AnalyseReceptionTests
{
    [Fact]
    public async Task Signale_la_date_differente_et_le_lot_bloque_par_ligne()
    {
        var achats = new AchatsEnMemoire();
        achats.MemeNumero.Add(new(1, new DateOnly(2027, 11, 30), false, 5, 200, true));
        var d = new DateOnly(2028, 5, 31);
        var conflits = await new AnalyserReception(achats, new DroitsFactices("achats.receptionner")).ExecuterAsync(1,
            [new Pharma.Domain.Achats.LigneReceptionSaisie(3, "Amlodipine", "am55", d, 2, 0)], 2);

        Assert.Equal([Pharma.Domain.Stock.ConflitLot.DateDifferente, Pharma.Domain.Stock.ConflitLot.LotBloque], conflits.Select(c => c.Type));
        Assert.All(conflits, c => Assert.Equal(0, c.Index));
        Assert.Contains("30/11/2027", conflits[0].Message);
        Assert.Contains("AM55", conflits[0].Message);
    }
}


public class LotsAEtiqueterPageTests
{
    private static AchatsEnMemoire Achats()
    {
        var a = new AchatsEnMemoire();
        a.AEtiqueter.AddRange(
        [
            new(1, "Amlodipine 5 mg cp", "AM55", new DateOnly(2027, 11, 30), 4, 4),
            new(2, "Oméprazole 20 mg gél", "OMZ7", new DateOnly(2027, 11, 9), 10, 10),
            new(3, "Paracétamol 500 mg cp", "P8831", new DateOnly(2029, 4, 26), 300, 297)
        ]);
        return a;
    }

    [Fact]
    public async Task Recherche_sans_accents_sur_produit_ou_lot_et_pagination()
    {
        var lister = new ListerLotsAEtiqueter(Achats());
        Assert.Equal([2L], (await lister.PageAsync("OMEPRAZOLE", null, null, new DemandePage())).Lignes.Select(l => l.LotId));
        Assert.Equal([1L], (await lister.PageAsync("am55", null, null, new DemandePage())).Lignes.Select(l => l.LotId));
        var p = await lister.PageAsync(null, null, null, new DemandePage(2, 5));
        Assert.Equal((3, 0), (p.Total, p.Lignes.Count));   // total de tout le filtre ; page au-delà de la fin vide
        Assert.Equal(2, (await lister.PageAsync(null, new DateOnly(2027, 12, 31), null, new DemandePage())).Total);
        Assert.Equal([3L], (await lister.PageAsync(null, null, 3, new DemandePage())).Lignes.Select(l => l.LotId));
    }
}

public class SuggestionsCommandePageTests
{
    private static AchatsEnMemoire Achats()
    {
        var a = new AchatsEnMemoire();
        a.Suggestions.AddRange(
        [
            new(1, "Amlodipine 5 mg cp", "rupture", 0, 10, 120m, 7, "Biopharm"),
            new(2, "Oméprazole 20 mg gél", "critique", 2, 8, 300m, 8, "Hydrapharm"),
            new(3, "Paracétamol 500 mg cp", "critique", 3, 20, null, null, null),
            new(4, "Ibuprofène 400 mg cp", "rupture", 0, 12, 90m, 7, "Biopharm")
        ]);
        return a;
    }

    [Fact]
    public async Task Filtres_produit_etat_grossiste_et_pagination()
    {
        var lister = new ListerSuggestionsCommande(Achats());
        Assert.Equal([2L], (await lister.PageAsync("OMEPRAZOLE", null, null, new DemandePage())).Page.Lignes.Select(s => s.ProduitId));
        Assert.Equal([1L, 4L], (await lister.PageAsync(null, "rupture", null, new DemandePage())).Page.Lignes.Select(s => s.ProduitId));
        Assert.Equal([1L, 4L], (await lister.PageAsync(null, null, 7, new DemandePage())).Page.Lignes.Select(s => s.ProduitId));
        Assert.Equal([3L], (await lister.PageAsync(null, null, 0, new DemandePage())).Page.Lignes.Select(s => s.ProduitId));   // jamais acheté
        var p = await lister.PageAsync(null, null, null, new DemandePage(2, 3));
        Assert.Equal((4, 1), (p.Page.Total, p.Page.Lignes.Count));
        Assert.Equal(["Biopharm", "Hydrapharm"], p.Grossistes.Select(g => g.Nom));   // sans doublon, par nom
        Assert.True(p.SansAchat);
    }
}
