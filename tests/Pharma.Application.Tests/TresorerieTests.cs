using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Tresorerie;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class TresorerieEnMemoire : ITresorerie
{
    public List<CompteTresorerie> Comptes { get; } =
    [
        new(1, "coffre", "Coffre", null, null, false, true, 0, new DateOnly(2026, 11, 1), 10000),
        new(2, "banque", "Banque fictive", "Banque", null, true, true, 0, new DateOnly(2026, 11, 1), 50000)
    ];
    public List<CaisseAVerser> Caisses { get; } = [];
    public List<MouvementAEcrire> Mouvements { get; } = [];
    public List<MouvementTresorerie> Lus { get; } = [];
    public List<(long Session, decimal Montant, bool Pendant, bool Auto)> Versements { get; } = [];
    public (long De, long Vers, decimal Montant)? Transfert { get; private set; }

    public Task<IReadOnlyList<CompteTresorerie>> ComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteTresorerie>>(Comptes);
    public Task<IReadOnlyList<MouvementTresorerie>> MouvementsAsync(long c, DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MouvementTresorerie>>(Lus);
    public Task<AVerser> AVerserAsync(CancellationToken ct = default) => Task.FromResult(new AVerser([], Caisses, []));
    public Task<long> EnregistrerCompteAsync(CompteAEcrire c, CancellationToken ct = default) => Task.FromResult(3L);
    public Task<long> AjouterMouvementAsync(MouvementAEcrire m, CancellationToken ct = default) { Mouvements.Add(m); return Task.FromResult(10L); }
    public Task<long> TransfererAsync(long de, long vers, DateOnly d, decimal m, string l, string? r, long u, CancellationToken ct = default)
    { Transfert = (de, vers, m); return Task.FromResult(11L); }
    public Task<long> RemettreChequesAsync(long c, DateOnly d, string? r, IReadOnlyList<long> p, long u, CancellationToken ct = default) => Task.FromResult(12L);
    public Task<long> VerserCaisseAsync(long s, long coffre, decimal m, DateOnly d, bool pendant, bool auto, long u, CancellationToken ct = default)
    { Versements.Add((s, m, pendant, auto)); return Task.FromResult(13L); }
    public Task<IReadOnlyList<(string Facture, decimal Montant)>> DettesSoldeesAsync(long mouvementId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(string, decimal)>>([]);
}

public class TresorerieTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 9));

    [Fact]
    public async Task Mouvements_pagines_et_filtres_par_employe_avec_totaux_de_tout_le_filtre()
    {
        var t = new TresorerieEnMemoire();
        var jour = new DateOnly(2026, 11, 5);
        for (var i = 0; i < 12; i++)
            t.Lus.Add(new MouvementTresorerie(jour, i % 3 == 0 ? "sortie" : "entree", 100, "versement_caisse", null, $"M{i}", null, i % 2 == 0 ? "Karim" : "Amina", "saisi"));
        t.Lus.Add(new MouvementTresorerie(jour, "entree", 50, "carte", null, "Cartes du jour", null, null, "calcule"));
        var cas = new ConsulterTresorerie(t, new DroitsFactices("tresorerie.gerer"));

        var tous = await cas.PageMouvementsAsync(1, jour, jour, null, new DemandePage(2, 5), 9);
        Assert.Equal(13, tous.Page.Total);
        Assert.Equal(["M5", "M6", "M7", "M8", "M9"], tous.Page.Lignes.Select(m => m.Libelle));
        Assert.Equal(["Amina", "Karim"], tous.Employes);   // employés de la période, triés ; un mouvement calculé n'a pas d'employé

        var karim = await cas.PageMouvementsAsync(1, jour, jour, "Karim", new DemandePage(2, 5), 9);
        Assert.Equal(6, karim.Page.Total);
        Assert.Single(karim.Page.Lignes);                  // 6e mouvement de Karim seul en page 2
        Assert.Equal(400, karim.TotalEntrees);             // totaux de tout le filtre, pas de la seule page
        Assert.Equal(200, karim.TotalSorties);
        Assert.Equal(["Amina", "Karim"], karim.Employes);  // la liste du filtre ne se réduit pas à l'employé choisi

        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterTresorerie(t, new DroitsFactices("caisse.vendre")).PageMouvementsAsync(1, jour, jour, null, new DemandePage(), 9));
    }

    [Fact]
    public async Task Cloture_automatique_porte_le_compte_moins_le_fond_au_coffre()
    {
        var t = new TresorerieEnMemoire();
        t.Caisses.Add(new CaisseAVerser(4, "Caisse 1", DateTime.UtcNow, "Vendeur A", 23500, 18500));
        var sessions = new SessionsEnMemoire { Parametres = new(true, 5000, 500, 0, "automatique") };
        var verse = await new VerserCaisseAuCoffre(t, sessions, new DroitsFactices(), Horloge, new JournalEspion()).ApresClotureAutomatiqueAsync(4, 5);
        Assert.Equal(18500m, verse);
        Assert.Equal((4L, 18500m, false, true), t.Versements.Single());
    }

    [Fact]
    public async Task Cloture_manuelle_ne_verse_rien()
    {
        var t = new TresorerieEnMemoire();
        t.Caisses.Add(new CaisseAVerser(4, "Caisse 1", DateTime.UtcNow, "Vendeur A", 23500, 18500));
        var verse = await new VerserCaisseAuCoffre(t, new SessionsEnMemoire(), new DroitsFactices(), Horloge, new JournalEspion()).ApresClotureAutomatiqueAsync(4, 5);
        Assert.Equal(0m, verse);
        Assert.Empty(t.Versements);
    }

    [Fact]
    public async Task Argent_porte_au_coffre_pendant_la_journee_au_plus_le_tiroir()
    {
        var t = new TresorerieEnMemoire();
        var cas = new VerserCaisseAuCoffre(t, new SessionsEnMemoire { Especes = 3000 }, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion());
        await cas.PendantJourneeAsync(6000, "Caisse 1", 5);
        Assert.Equal((1L, 6000m, true, false), t.Versements.Single());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.PendantJourneeAsync(9000, "Caisse 1", 5));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new VerserCaisseAuCoffre(t, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"), Horloge, new JournalEspion()).PendantJourneeAsync(100, "Caisse 1", 5));
    }

    [Fact]
    public async Task Depense_sortie_du_compte_et_solde_suffisant()
    {
        var t = new TresorerieEnMemoire();
        var cas = new SaisirMouvementTresorerie(t, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion());
        await cas.ExecuterAsync(1, null, "depense", 2500, "energie", "Facture électricité", null, null, null, null, 5);
        Assert.Equal(("sortie", "depense", "energie"), (t.Mouvements[0].Sens, t.Mouvements[0].Nature, t.Mouvements[0].Categorie));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, null, "depense", 12000, "loyer", "Loyer", null, null, null, null, 5));
    }

    [Fact]
    public async Task Versement_organisme_impute_aux_bordereaux_en_entier_ou_en_partie()
    {
        var t = new TresorerieEnMemoire();
        var b = new BordereauxEnMemoire();
        b.Ajouter(7, organisme: 1, statut: "depose", attendu: 48000);
        b.Ajouter(8, organisme: 1, statut: "paye_partiel", attendu: 10000, verse: 4000);
        b.Ajouter(9, organisme: 2, statut: "depose", attendu: 5000);
        var cas = new SaisirMouvementTresorerie(t, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion(), bordereaux: b);

        // Ancien appel (un bordereau, statut ignoré) : tout le versement sur ce bordereau
        await cas.ExecuterAsync(2, null, "versement_organisme", 48000, null, null, "VIR-1", 1, 7, null, 5);
        Assert.Equal([(7L, 48000m)], t.Mouvements[0].Bordereaux!);
        Assert.Equal(7L, t.Mouvements[0].BordereauId);

        // Un virement pour deux bordereaux, le second en partie ; l'écart reste à justifier
        await cas.ExecuterAsync(2, null, "versement_organisme", 20000, null, null, "VIR-2", 1, null, null, 5, null, [(7L, 15000m), (8L, 3000m)]);
        Assert.Equal([(7L, 15000m), (8L, 3000m)], t.Mouvements[1].Bordereaux!);
        Assert.Null(t.Mouvements[1].BordereauId);

        // Au-delà du reste, autre organisme, total supérieur au versement, ou factures et bordereaux ensemble : refusés
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 9000, null, null, null, 1, null, null, 5, null, [(8L, 7000m)]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 5000, null, null, null, 1, null, null, 5, null, [(9L, 5000m)]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 1000, null, null, null, 1, null, null, 5, null, [(7L, 2000m)]));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 1000, null, null, null, 1, null, null, 5, [3L], [(7L, 1000m)]));
    }

    [Fact]
    public async Task Transfert_limite_au_solde_du_compte_source()
    {
        var t = new TresorerieEnMemoire();
        var cas = new TransfererTresorerie(t, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion());
        await cas.ExecuterAsync(1, 2, null, 8000, "Dépôt", 5);
        Assert.Equal((1L, 2L, 8000m), t.Transfert);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, 2, null, 20000, null, 5));
    }
}
