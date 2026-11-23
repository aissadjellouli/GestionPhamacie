using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Correctifs de la revue de code du 2026-11-23 : règles pures du domaine.</summary>
public class CorrectifsRevueTests
{
    [Fact]
    public void Une_prestation_ne_compte_pas_dans_le_solde_exact_du_dernier_retour()
    {
        // 500 DA de produit + 200 DA de prestation : part patient 700 ; dernier retour de la boîte : 500 DA, pas 700
        var partProduits = RegleRetour.PartPatientProduits(700, 200);
        Assert.Equal(500m, partProduits);
        Assert.Equal(500m, RegleRetour.TotalARendre(500, partProduits, 0, toutRendu: true));
        // Après un premier retour de 200 DA, le solde est le reste des seuls produits
        Assert.Equal(300m, RegleRetour.TotalARendre(299.99m, RegleRetour.PartPatientProduits(700, 200), 200, toutRendu: true));
        // Sans prestation : comportement inchangé
        Assert.Equal(700m, RegleRetour.PartPatientProduits(700, 0));
    }

    [Fact]
    public void Montant_d_un_encaissement_de_dette_au_centime_pres()
    {
        RegleReglementCredit.Verifier(100.25m, 500, "especes");
        var e = Assert.Throws<RegleMetierException>(() => RegleReglementCredit.Verifier(100.255m, 500, "especes"));
        Assert.Equal("montant", e.Champ);
    }

    [Fact]
    public void Montant_d_avoir_au_centime_pres()
    {
        RegleAvoir.VerifierUtilisation(10.5m, 100, 200, patientConnu: true);
        var e = Assert.Throws<RegleMetierException>(() => RegleAvoir.VerifierUtilisation(10.505m, 100, 200, patientConnu: true));
        Assert.Equal("avoir", e.Champ);
    }

    [Fact]
    public void Part_organisme_repartie_reste_dans_les_bornes_de_chaque_ligne_et_garde_le_total()
    {
        // Cas où l'écart d'arrondi de la dernière ligne dépasse son montant (0,11 pour 0,10) : l'excédent est reporté sur les précédentes
        decimal[] montants = [0.09m, 0.07m, 0.06m, 0.10m, 0.05m, 0.18m, 0.07m, 0.09m, 0.10m];
        var parts = Vente.RepartirPartOrganisme(0.74m, montants);
        Assert.Equal(0.74m, parts.Sum());
        for (var i = 0; i < parts.Count; i++) Assert.InRange(parts[i], 0m, montants[i]);
        Assert.Equal(0.10m, parts[^1]);
    }

    [Fact]
    public void Part_organisme_jamais_negative_sur_la_derniere_ligne()
    {
        var parts = Vente.RepartirPartOrganisme(0.01m, [0.01m, 0.01m, 0.01m]);
        Assert.Equal(0.01m, parts.Sum());
        Assert.All(parts, p => Assert.InRange(p, 0m, 0.01m));
    }
}
