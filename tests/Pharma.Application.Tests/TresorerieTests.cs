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
    public List<(long Session, decimal Montant, bool Pendant, bool Auto)> Versements { get; } = [];
    public (long De, long Vers, decimal Montant)? Transfert { get; private set; }

    public Task<IReadOnlyList<CompteTresorerie>> ComptesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<CompteTresorerie>>(Comptes);
    public Task<IReadOnlyList<MouvementTresorerie>> MouvementsAsync(long c, DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MouvementTresorerie>>([]);
    public Task<AVerser> AVerserAsync(CancellationToken ct = default) => Task.FromResult(new AVerser([], Caisses, []));
    public Task<long> EnregistrerCompteAsync(CompteAEcrire c, CancellationToken ct = default) => Task.FromResult(3L);
    public Task<long> AjouterMouvementAsync(MouvementAEcrire m, CancellationToken ct = default) { Mouvements.Add(m); return Task.FromResult(10L); }
    public Task<long> TransfererAsync(long de, long vers, DateOnly d, decimal m, string l, string? r, long u, CancellationToken ct = default)
    { Transfert = (de, vers, m); return Task.FromResult(11L); }
    public Task<long> RemettreChequesAsync(long c, DateOnly d, string? r, IReadOnlyList<long> p, long u, CancellationToken ct = default) => Task.FromResult(12L);
    public Task<long> VerserCaisseAsync(long s, long coffre, decimal m, DateOnly d, bool pendant, bool auto, long u, CancellationToken ct = default)
    { Versements.Add((s, m, pendant, auto)); return Task.FromResult(13L); }
}

public class TresorerieTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 9));

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
    public async Task Versement_organisme_avec_bordereau_demande_son_statut()
    {
        var t = new TresorerieEnMemoire();
        var cas = new SaisirMouvementTresorerie(t, new DroitsFactices("tresorerie.gerer"), Horloge, new JournalEspion());
        await cas.ExecuterAsync(2, null, "versement_organisme", 48000, null, null, "VIR-1", 1, 7, "paye", 5);
        Assert.Equal(("entree", 1L, 7L, "paye"), (t.Mouvements[0].Sens, t.Mouvements[0].OrganismeId!.Value, t.Mouvements[0].BordereauId!.Value, t.Mouvements[0].StatutBordereau!));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, null, "versement_organisme", 48000, null, null, null, 1, 7, null, 5));
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
