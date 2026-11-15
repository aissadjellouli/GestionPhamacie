using Pharma.Domain.Caisse;
using Pharma.Domain.Factures;

namespace Pharma.Domain.Tests;

/// <summary>Recette du 15/11/2026 : format rouleau du ticket, mentions de la facture d'organisme.</summary>
public class Recette20261115Tests
{
    [Theory]
    [InlineData(57, 57)]
    [InlineData(58, 58)]
    [InlineData(76, 76)]
    [InlineData(80, 80)]
    [InlineData(112, 112)]
    [InlineData(65, 80)]
    [InlineData(null, 80)]
    [InlineData(0, 80)]
    public void Largeur_du_rouleau_bornee_aux_largeurs_du_marche(int? demandee, int attendue) =>
        Assert.Equal(attendue, FormatTicket.Borner(demandee));

    [Fact]
    public void Rouleau_etroit_en_57_et_58_mm()
    {
        Assert.True(FormatTicket.Etroit(57));
        Assert.True(FormatTicket.Etroit(58));
        Assert.False(FormatTicket.Etroit(80));
    }

    [Fact]
    public void Mention_organisme_patient_assure_ordonnance_medecin()
    {
        Assert.Equal("patient Fictif Un, n° d’assuré 123, ordonnance ORD-1, médecin Dr Kaci",
            RegleFacture.MentionOrganisme("Fictif Un", "123", "ORD-1", "Dr Kaci"));
        Assert.Equal("patient Fictif Un", RegleFacture.MentionOrganisme("Fictif Un", " ", null, null));
        Assert.Null(RegleFacture.MentionOrganisme(null, null, null, null));
    }

    private static readonly LigneVendue[] Vente = [new(1, "T-000001", 10, "Produit A", 0, 1, 20, 80, 0)];
    private static readonly Dictionary<long, string> Mentions = new() { [1] = "patient Fictif Un, ordonnance ORD-1" };

    [Fact]
    public void La_facture_d_organisme_porte_patient_et_ordonnance_sur_chaque_ligne()
    {
        var l = RegleFacture.DepuisVentes(Destinataire.Organisme, Vente, Mentions).Single();
        Assert.Equal("Produit A (vente T-000001 · patient Fictif Un, ordonnance ORD-1)", l.Designation);
    }

    [Fact]
    public void Jamais_de_donnee_de_sante_sur_une_facture_de_patient()
    {
        var l = RegleFacture.DepuisVentes(Destinataire.Patient, Vente, Mentions).Single();
        Assert.Equal("Produit A (vente T-000001)", l.Designation);
    }
}
