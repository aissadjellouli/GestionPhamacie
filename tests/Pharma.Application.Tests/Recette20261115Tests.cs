using Pharma.Application.Caisse;
using Pharma.Domain.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class RecusEnMemoire(RecuCreditEnregistre? recu) : IRecusCredit
{
    public Task<RecuCreditEnregistre?> LireAsync(string numero, CancellationToken ct = default) =>
        Task.FromResult(recu?.Numero == numero ? recu : null);
}

internal sealed class DocumentsTicketEspion : IDocumentsTicket
{
    public TicketVente? Ticket_;
    public int Largeur;
    public byte[] Ticket(TicketVente t, decimal? rendu, int largeurMm = FormatTicket.LargeurParDefaut)
    {
        (Ticket_, Largeur) = (t, largeurMm);
        return [1, 2, 3];
    }
}

/// <summary>Recette du 15/11/2026 : reçu de crédit en PDF au format rouleau.</summary>
public class Recette20261115Tests
{
    private static readonly RecuCreditEnregistre Recu = new("RC-000199", DateTime.UtcNow, 100m, "especes", 442.5m, "Dr Titulaire", "ADH");

    [Fact]
    public async Task Le_recu_de_credit_est_produit_par_le_serveur_sans_le_nom_du_patient()
    {
        var docs = new DocumentsTicketEspion();
        var (contenu, nom) = (await new ImprimerRecuCredit(new RecusEnMemoire(Recu), docs, new PharmacieFactice()).ExecuterAsync("RC-000199", 58))!.Value;
        Assert.Equal([1, 2, 3], contenu);
        Assert.Equal("recu_RC-000199.pdf", nom);
        Assert.Equal(58, docs.Largeur);
        var t = docs.Ticket_!;
        Assert.Equal("Pharmacie Fictive", t.Pharmacie);
        Assert.Equal(100m, t.PartPatient);
        Assert.Contains("reste dû 442,50", t.Lignes.Single().Produit.Replace(' ', ' ').Replace(' ', ' '));
    }

    [Fact]
    public async Task Une_largeur_hors_liste_revient_a_80_mm()
    {
        var docs = new DocumentsTicketEspion();
        await new ImprimerRecuCredit(new RecusEnMemoire(Recu), docs, new PharmacieFactice()).ExecuterAsync("RC-000199", 65);
        Assert.Equal(80, docs.Largeur);
    }

    [Fact]
    public async Task Recu_inconnu_introuvable_revue_M5()
    {
        Assert.Null(await
            new ImprimerRecuCredit(new RecusEnMemoire(null), new DocumentsTicketEspion(), new PharmacieFactice()).ExecuterAsync("RC-999999", 80));   // 404 côté API
    }
}
