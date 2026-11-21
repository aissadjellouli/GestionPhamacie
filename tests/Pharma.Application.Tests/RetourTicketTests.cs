using Pharma.Application.Caisse;

namespace Pharma.Application.Tests;

/// <summary>Ligne « Retour du … » du ticket (imprimé et PDF) : mention de la part organisme retirée et rappel Chifa (script 155).</summary>
public class RetourTicketTests
{
    private static readonly DateTime Quand = new(2026, 11, 21, 10, 30, 0);

    [Fact]
    public void Retour_avec_part_organisme_porte_la_mention()
    {
        var l = RetourTicket.Libelle(Quand, "Produit A × 1", 30m, 0, 0, 70m);
        Assert.Contains(RetourTicket.PartOrganisme(70m), l);
        Assert.StartsWith("Part organisme retirée : ", RetourTicket.PartOrganisme(70m));
        Assert.DoesNotContain(RetourTicket.RappelChifa, l);
    }

    [Fact]
    public void Vente_Chifa_non_corrigee_ajoute_le_rappel()
    {
        var l = RetourTicket.Libelle(Quand, "Produit A × 1", 30m, 0, 0, 70m, chifaACorriger: true);
        Assert.Contains(RetourTicket.PartOrganisme(70m), l);
        Assert.EndsWith(RetourTicket.RappelChifa, l);
    }

    [Fact]
    public void Retour_sans_part_organisme_inchange()
    {
        var l = RetourTicket.Libelle(Quand, "Produit A × 1", 30m, 0, 0, 0, chifaACorriger: true);
        Assert.DoesNotContain("Part organisme", l);
        Assert.DoesNotContain(RetourTicket.RappelChifa, l);
        Assert.Equal($"Retour du 21/11/2026 10:30 : Produit A × 1 · rendu {30m:N2} DA", l);
    }

    [Fact]
    public void Taux_100_rien_rendu_mais_part_organisme_mentionnee()
    {
        var l = RetourTicket.Libelle(Quand, null, 0, 0, 0, 100m);
        Assert.Contains($"rendu {0m:N2} DA", l);
        Assert.Contains(RetourTicket.PartOrganisme(100m), l);
        Assert.Contains(" : produits", l);
    }

    [Fact]
    public void Avoir_et_dette_gardent_leur_forme()
    {
        var l = RetourTicket.Libelle(Quand, "Produit A × 2", 0, 15m, 10m);
        Assert.Contains($"recrédité en avoir {10m:N2} DA", l);
        Assert.Contains($"déduit de la dette {15m:N2} DA", l);
        Assert.DoesNotContain("rendu", l);
    }
}
