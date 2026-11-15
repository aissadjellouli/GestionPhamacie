using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

/// <summary>Prestations sans stock (2026-11-15, script 105).</summary>
public class PrestationsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);
    private static readonly TypePrestation Tension = new(1, "TENSION", "Prise de tension", 200m, 19m, true);

    private static Produit Amoxicilline => new()
    {
        Id = 2, NomCommercial = "Amoxicilline 1 g", PrixPublic = 350m, TarifReference = 300m, Remboursable = true, Classe = ClasseReglementaire.Libre
    };

    private static Lot LotDe(Produit p) => new()
    {
        Id = p.Id * 10, ProduitId = p.Id, NumeroLot = "L" + p.Id, QuantiteDisponible = 10, DatePeremption = Aujourdhui.AddDays(200)
    };

    [Fact]
    public void Ligne_de_prestation_sans_produit_ni_lot_avec_libelle_et_tva_figes()
    {
        var v = new Vente();
        var l = v.AjouterPrestation(Tension, 2);
        Assert.True(l.EstPrestation);
        Assert.Equal((null, null, 1L, "Prise de tension", 19m), (l.ProduitId, l.LotId, l.TypePrestationId, l.Libelle, l.TauxTva));
        Assert.Equal((200m, 0m, 0m, 400m), (l.PrixUnitaire, l.Shp, l.MontantOrganisme, l.MontantPatient));
        Assert.Equal((400m, 400m), (v.MontantTotal, v.PartPatient));
    }

    [Fact]
    public void Vente_mixte_produit_et_prestation_les_montants_du_produit_sont_inchanges()
    {
        var seul = new Vente();
        seul.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);
        var mixte = new Vente();
        mixte.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);
        mixte.AjouterPrestation(Tension, 1);
        Assert.Equal(seul.Lignes[0].MontantPatient, mixte.Lignes[0].MontantPatient);
        Assert.Equal(seul.MontantTotal + 200m, mixte.MontantTotal);
        Assert.Equal(mixte.Lignes.Sum(x => x.PrixUnitaire * x.Quantite), mixte.MontantTotal);
    }

    [Fact]
    public void Tiers_payant_l_organisme_ne_paie_jamais_la_prestation()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80, PatientId = 1, OrganismeId = 1 };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);   // 300 x 80 % = 240 pour l'organisme
        v.AjouterPrestation(Tension, 1);
        Assert.Equal((240m, 550m, 310m), (v.PartOrganisme, v.MontantTotal, v.PartPatient));
        Assert.Equal(0m, v.Lignes[1].MontantOrganisme);
        // Reste à charge saisi : au moins les lignes non prises en charge (produit non remboursable et prestation)
        Assert.Throws<RegleMetierException>(() => v.SaisirResteACharge(150m));
        v.SaisirResteACharge(250m);
        Assert.Equal((0m, 300m), (v.Lignes[1].MontantOrganisme, v.PartOrganisme));
    }

    [Fact]
    public void Remise_du_vendeur_appliquee_a_la_prestation_comme_a_un_produit_hors_medicament()
    {
        var v = new Vente();
        v.AjouterPrestation(Tension, 1);
        v.AppliquerRemise(10);
        Assert.Equal((20m, 180m, 0m), (v.MontantRemise, v.MontantTotal, v.MontantDon));
    }

    [Theory]
    [InlineData(0, true, 1)]      // prix non fixé
    [InlineData(200, false, 1)]   // désactivée
    [InlineData(200, true, 0)]    // quantité nulle
    public void Prestation_refusee(decimal prix, bool actif, int quantite) =>
        Assert.Throws<RegleMetierException>(() => new Vente().AjouterPrestation(Tension with { Prix = prix, Actif = actif }, quantite));

    [Fact]
    public void Remises_automatiques_ignorent_la_prestation()
    {
        var v = new Vente();
        v.AjouterPrestation(Tension, 1);
        v.AppliquerRemisesAutomatiques(new Dictionary<long, decimal> { [1] = 50 });   // 1 = id d'un produit, pas de la prestation
        Assert.Equal(0m, v.Lignes[0].RemiseAutoPct);
    }

    [Theory]
    [InlineData("", "Libellé", 0, 0, "code")]
    [InlineData("CODE-TROP-LONG-POUR-ETRE-ACCEPTE", "Libellé", 0, 0, "code")]
    [InlineData("C", " ", 0, 0, "libelle")]
    [InlineData("C", "L", -1, 0, "prix")]
    [InlineData("C", "L", 10.555, 0, "prix")]
    [InlineData("C", "L", 0, 101, "taux_tva")]
    public void Saisie_du_type_de_prestation_refusee(string code, string libelle, decimal prix, decimal tva, string champ) =>
        Assert.Equal(champ, Assert.Throws<RegleMetierException>(() => RegleTypePrestation.Verifier(code, libelle, prix, tva)).Champ);

    [Fact]
    public void Saisie_du_type_de_prestation_normalisee() =>
        Assert.Equal(("GLYC", "Mesure de la glycémie"), RegleTypePrestation.Verifier(" glyc ", " Mesure de la glycémie ", 150m, 19m));
}
