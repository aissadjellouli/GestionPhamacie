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

    // Retour d'une vente en tiers payant (script 155) : la mention de la part organisme retirée et le rappel Chifa allongent la ligne du
    // retour sur le ticket PDF ; en démonstration, le filigrane reste posé
    private static readonly DateTime QuandRetour = new(2026, 11, 21, 10, 30, 0);

    [Fact]
    public void Retour_avec_part_organisme_mentionne_sur_le_ticket_PDF()
    {
        var sans = Ticket(1) with { Retours = [RetourTicket.Libelle(QuandRetour, "Produit fictif 1 × 1", 30m, 0, 0)] };
        var avec = Ticket(1) with { Retours = [RetourTicket.Libelle(QuandRetour, "Produit fictif 1 × 1", 30m, 0, 0, 70m, chifaACorriger: true)] };
        Assert.Contains("Part organisme retirée", avec.Retours![0]);
        Assert.DoesNotContain("Part organisme retirée", sans.Retours![0]);
        using var a = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(avec, null, 58)), PdfDocumentOpenMode.Import);
        using var s = PdfReader.Open(new MemoryStream(new DocumentsPdf().Ticket(sans, null, 58)), PdfDocumentOpenMode.Import);
        Assert.Equal(1, a.PageCount);
        Assert.True(a.Pages[0].Height.Millimeter > s.Pages[0].Height.Millimeter);
    }

    [Fact]
    public void Ticket_PDF_d_un_retour_avec_part_organisme_garde_le_filigrane_en_demonstration()
    {
        var t = Ticket(1) with { Retours = [RetourTicket.Libelle(QuandRetour, "Produit fictif 1 × 1", 0, 0, 0, 70m, chifaACorriger: true)] };
        var demo = new DocumentsPdf(new FiligranePdf(true)).Ticket(t, null, 80);
        var normal = new DocumentsPdf().Ticket(t, null, 80);
        using var d = PdfReader.Open(new MemoryStream(demo), PdfDocumentOpenMode.Import);
        using var n = PdfReader.Open(new MemoryStream(normal), PdfDocumentOpenMode.Import);
        Assert.Equal(FiligranePdf.Texte, d.Info.Keywords);
        Assert.Equal(FiligranePdf.Mention, d.Info.Subject);
        Assert.NotEqual(FiligranePdf.Texte, n.Info.Keywords);
        Assert.True(demo.Length > normal.Length);
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
