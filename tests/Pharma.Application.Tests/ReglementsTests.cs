using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class ReglementsEnMemoire(SituationGrossiste s) : IReglementsFournisseurs
{
    public ReglementAEcrire? Ecrit { get; private set; }
    public Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GrossisteAReglement>>([]);
    public Task<SituationGrossiste?> SituationAsync(long id, CancellationToken ct = default) => Task.FromResult<SituationGrossiste?>(s);
    public Task<long> EnregistrerAsync(ReglementAEcrire r, CancellationToken ct = default) { Ecrit = r; return Task.FromResult(1L); }
}

internal sealed class CreditsEnMemoire(decimal solde) : ICreditsClients
{
    public (decimal Montant, string Mode, long Session)? Encaisse { get; private set; }
    public Task<IReadOnlyList<PatientEnCredit>> AvecCreditAsync(string? r, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatientEnCredit>>([]);
    public Task<SituationCredit?> SituationAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<SituationCredit?>(new SituationCredit(new PatientEnCredit(id, "Patient fictif", null, solde, 5000, null), []));
    public Task<long> EncaisserAsync(long p, decimal m, string mode, long s, long u, CancellationToken ct = default) { Encaisse = (m, mode, s); return Task.FromResult(7L); }
}

public class ReglementsTests
{
    private static readonly SituationGrossiste Grossiste = new(4, "Grossiste fictif", 1200,
        [new FactureFournisseur(10, "F-1", "BL-1", new DateTime(2026, 10, 1), 500, 0, 500), new FactureFournisseur(11, "F-2", "BL-2", new DateTime(2026, 11, 1), 700, 0, 700)], []);

    private static EnregistrerReglement Cas(ReglementsEnMemoire stock, string droit = "achats.regler", SessionsEnMemoire? sessions = null) =>
        new(stock, sessions ?? new SessionsEnMemoire(), new DroitsFactices(droit), new HorlogeFixe(new DateOnly(2026, 11, 9)), new JournalEspion());

    [Fact]
    public async Task Sans_facture_choisie_les_plus_anciennes_sont_reglees()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        await Cas(stock).ExecuterAsync(4, null, 600, "cheque", "CHQ-001", "Banque fictive", null, 0, null, null, 1, "Caisse 1");
        Assert.Equal([(10L, 500m), (11L, 100m)], stock.Ecrit!.Imputations);
        Assert.Null(stock.Ecrit.SessionCaisseId);
    }

    [Fact]
    public async Task Droit_requis_et_montant_au_plus_la_dette()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste), "achats.commander").ExecuterAsync(4, null, 100, "especes", null, null, null, 0, null, null, 1, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste)).ExecuterAsync(4, null, 1300, "especes", null, null, null, 0, null, null, 1, "Caisse 1"));
    }

    [Fact]
    public async Task Complement_du_tiroir_rattache_a_la_caisse_ouverte_du_poste()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        await Cas(stock).ExecuterAsync(4, null, 500, "especes", null, null, null, 200, null, [new(10, 500)], 1, "Caisse 1");
        Assert.Equal((200m, (long?)1), (stock.Ecrit!.PartTiroir, stock.Ecrit.SessionCaisseId));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste), sessions: new SessionsEnMemoire { Ouverte = null })
                .ExecuterAsync(4, null, 500, "especes", null, null, null, 200, null, null, 1, "Caisse 1"));
    }

    [Fact]
    public async Task Credit_client_encaisse_dans_la_caisse_du_poste()
    {
        var stock = new CreditsEnMemoire(210);
        var recu = await new EncaisserReglementCredit(stock, new SessionsEnMemoire(), new JournalEspion()).ExecuterAsync(3, 200, "especes", 5, "Caisse 1");
        Assert.Equal((7L, 10m), (recu.Numero, recu.ResteDu));
        Assert.Equal((200m, "especes", 1L), stock.Encaisse);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new EncaisserReglementCredit(new CreditsEnMemoire(210), new SessionsEnMemoire { Ouverte = null }, new JournalEspion()).ExecuterAsync(3, 100, "especes", 5, "Caisse 1"));
    }
}
