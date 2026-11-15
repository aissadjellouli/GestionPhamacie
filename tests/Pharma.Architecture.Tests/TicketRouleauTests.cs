using Pharma.Application.Caisse;
using Pharma.Infrastructure.Impression;
using PdfSharp.Pdf.IO;

namespace Pharma.Architecture.Tests;

/// <summary>Ticket PDF au format rouleau (2026-11-15) : largeur du rouleau, une seule page dont la hauteur suit le contenu.</summary>
public class TicketRouleauTests
{
    private static TicketVente Ticket(int lignes) => new("T-000001", DateTime.UtcNow, "Dr Titulaire Fictif", "Caisse 1", "validee", null, null,
        Enumerable.Range(1, lignes).Select(i => new LigneTicket($"Produit fictif {i} 500 mg comprimés", 1, 100, 100, 0)).ToList(),
        100m * lignes, 0, 0, 100m * lignes, "especes", "Pharmacie Fictive", "1 rue Fictive", "000", "123");

    [Theory]
    [InlineData(57)]
    [InlineData(58)]
    [InlineData(76)]
    [InlineData(80)]
    [InlineData(112)]
    public void Page_de_la_largeur_du_rouleau(int largeur)
    {
        using var doc = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(Ticket(3), 20m, largeur)), PdfDocumentOpenMode.Import);
        Assert.Equal(1, doc.PageCount);
        Assert.Equal(largeur, doc.Pages[0].Width.Millimeter, 0.5);
    }

    [Fact]
    public void Largeur_hors_liste_ramenee_a_80_mm()
    {
        using var doc = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(Ticket(1), null, 65)), PdfDocumentOpenMode.Import);
        Assert.Equal(80, doc.Pages[0].Width.Millimeter, 0.5);
    }

    [Fact]
    public void Remis_a_ou_pour_ajoute_la_ligne_client()
    {
        var sans = new DocumentsPdf().Ticket(Ticket(1), null, 80);
        var avec = new DocumentsPdf().Ticket(Ticket(1) with { Client = "Karim Fictif · remis à Ali Fictif" }, null, 80);
        using var a = PdfReader.Open(new MemoryStream(avec), PdfDocumentOpenMode.Import);
        using var s = PdfReader.Open(new MemoryStream(sans), PdfDocumentOpenMode.Import);
        Assert.True(a.Pages[0].Height.Millimeter > s.Pages[0].Height.Millimeter);
    }

    [Fact]
    public void Hauteur_selon_le_contenu_sans_saut_de_page()
    {
        using var court = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(Ticket(1), null, 80)), PdfDocumentOpenMode.Import);
        using var long_ = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(Ticket(60), null, 80)), PdfDocumentOpenMode.Import);
        Assert.Equal(1, long_.PageCount);
        Assert.True(long_.Pages[0].Height.Millimeter > court.Pages[0].Height.Millimeter + 100);
        Assert.True(court.Pages[0].Height.Millimeter < 200);
        var dossier = Environment.GetEnvironmentVariable("PHARMA_TICKET_PDF_DOSSIER");
        if (dossier is not null)
            foreach (var l in new[] { 58, 80 })
                File.WriteAllBytes(Path.Combine(dossier, $"ticket_{l}.pdf"), new DocumentsPdf().Ticket(Ticket(3), 20m, l));
        if (dossier is not null)
            File.WriteAllBytes(Path.Combine(dossier, "ticket_58_long.pdf"),
                new DocumentsPdf().Ticket(Ticket(1) with { Vendeur = "Dr Titulaire Démo Fictive", Poste = "POSTE-COMPTOIR-01" }, null, 58));
    }
}
