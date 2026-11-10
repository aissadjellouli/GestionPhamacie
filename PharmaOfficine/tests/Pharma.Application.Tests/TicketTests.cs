using Pharma.Application.Administration;
using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class TicketsEnMemoire(TicketVente? ticket) : ITicketLecture
{
    public Task<TicketVente?> LireAsync(long venteId, CancellationToken ct = default) => Task.FromResult(ticket);
}

internal sealed class PharmacieFactice : IPharmacie
{
    public Task<IdentitePharmacie> LireAsync(CancellationToken ct = default) =>
        Task.FromResult(new IdentitePharmacie("Pharmacie Fictive", "Dr Fictif", "1 rue Fictive", "000 00 00 00", "000000000000000", null, "fr"));
    public Task EnregistrerAsync(IdentitePharmacie identite, CancellationToken ct = default) => Task.CompletedTask;
}

public class TicketTests
{
    [Fact]
    public async Task Ticket_complete_avec_les_coordonnees_de_la_pharmacie()
    {
        var vente = new TicketVente("T-000001", DateTime.UtcNow, "Vendeur", "Caisse 1", "validee", null, null,
            [new LigneTicket("Produit fictif", 2, 100, 200, 0)], 200, 0, 0, 200, "especes", null, null, null, null);
        var t = await new ObtenirTicket(new TicketsEnMemoire(vente), new PharmacieFactice()).ExecuterAsync(1);
        Assert.Equal(("Pharmacie Fictive", "1 rue Fictive", "000000000000000"), (t.Pharmacie, t.Adresse, t.Nif));
        Assert.Single(t.Lignes);
    }

    [Fact]
    public async Task Vente_introuvable() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new ObtenirTicket(new TicketsEnMemoire(null), new PharmacieFactice()).ExecuterAsync(9));
}
