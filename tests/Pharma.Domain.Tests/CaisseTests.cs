using Pharma.Domain.Caisse;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;
using Pharma.Domain.Ventes;

namespace Pharma.Domain.Tests;

public class CaisseTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    private static Produit Amoxicilline => new()
    {
        // Hors liste ici pour garder des prix ronds (sans SHP) ; le SHP est vérifié par les tests « Prix_de_vente_… »
        Id = 2, NomCommercial = "Amoxicilline 1 g", PrixPublic = 350m, TarifReference = 300m, Remboursable = true,
        Classe = ClasseReglementaire.Libre
    };

    private static Produit Alprazolam => new()
    {
        Id = 7, NomCommercial = "Alprazolam 0,25 mg", PrixPublic = 190m, Remboursable = false, Classe = ClasseReglementaire.Psychotrope
    };

    private static Lot LotDe(Produit p, int quantite = 10, int joursRestants = 200, bool bloque = false) => new()
    {
        Id = p.Id * 10, ProduitId = p.Id, NumeroLot = "L" + p.Id, QuantiteDisponible = quantite,
        DatePeremption = Aujourdhui.AddDays(joursRestants), Bloque = bloque
    };

    [Fact]
    public void Prix_de_vente_PPA_du_lot_plus_SHP_selon_la_liste()
    {
        var produit = new Produit { Id = 3, NomCommercial = "P", PrixPublic = 350m, Classe = ClasseReglementaire.ListeI };
        var lotSansPpa = LotDe(produit);
        var lotAvecPpa = new Lot { Id = 31, ProduitId = 3, NumeroLot = "L31", QuantiteDisponible = 5, DatePeremption = Aujourdhui.AddDays(200), Ppa = 360m };
        Assert.Equal(352.50m, PrixVente.Unitaire(produit, lotSansPpa));   // prix du catalogue + SHP liste I
        Assert.Equal(362.50m, PrixVente.Unitaire(produit, lotAvecPpa));   // PPA du lot + SHP
        var v = new Vente();
        var l = v.AjouterProduit(produit, lotAvecPpa, 2, Aujourdhui);
        Assert.Equal((362.50m, 2.50m, 725m), (l.PrixUnitaire, l.Shp, v.MontantTotal));
        var psy = new Produit { Id = 4, PrixPublic = 100m, Classe = ClasseReglementaire.Psychotrope, ListePsychotrope = "liste_ii" };
        Assert.Equal(101.50m, PrixVente.Unitaire(psy, LotDe(psy)));
    }

    [Fact]
    public void Reste_a_charge_saisi_le_taux_est_ignore_et_la_part_organisme_repartie()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = null, PatientId = 1, OrganismeId = 1 };
        var autre = new Produit { Id = 5, NomCommercial = "B", PrixPublic = 300m, Remboursable = false };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);   // 350
        v.AjouterProduit(autre, LotDe(autre), 1, Aujourdhui);                 // 300, non remboursable
        v.SaisirResteACharge(150m);
        Assert.Equal((150m, 500m, 650m), (v.PartPatient, v.PartOrganisme, v.MontantTotal));
        Assert.Equal(500m, v.Lignes.Sum(l => l.MontantOrganisme));
        Assert.Equal(269.23m, v.Lignes[0].MontantOrganisme);                  // 500 × 350 / 650
        v.VerifierAvantEncaissement(ordonnanceSaisie: false);                  // taux non exigé quand le reste est saisi
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(351)]
    public void Reste_a_charge_hors_bornes_refuse(decimal reste)
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80 };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);
        Assert.Throws<RegleMetierException>(() => v.SaisirResteACharge(reste));
        Assert.Throws<RegleMetierException>(() => new Vente().SaisirResteACharge(0));   // vente libre
    }

    [Fact]
    public void Produit_bloque_a_la_vente_refuse_avec_son_motif()
    {
        var bloque = new Produit { Id = 9, NomCommercial = "Bloqué", PrixPublic = 100m, BloqueVente = true, MotifBlocage = "rappel du fabricant" };
        var ex = Assert.Throws<RegleMetierException>(() => new Vente().AjouterProduit(bloque, LotDe(bloque), 1, Aujourdhui));
        Assert.Contains("rappel du fabricant", ex.Message);
    }

    [Fact]
    public void Vente_libre_le_patient_paie_tout()
    {
        var v = new Vente();
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 2, Aujourdhui);
        Assert.Equal((700m, 0m, 700m), (v.MontantTotal, v.PartOrganisme, v.PartPatient));
    }

    [Fact]
    public void Tiers_payant_sur_le_tarif_de_reference()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80 };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 2, Aujourdhui);
        Assert.Equal(480m, v.PartOrganisme);          // 300 x 2 x 80 %
        Assert.Equal(220m, v.PartPatient);            // 700 - 480
    }

    [Fact]
    public void Produit_non_remboursable_hors_tiers_payant()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 100 };
        v.AjouterProduit(Alprazolam, LotDe(Alprazolam), 1, Aujourdhui);
        Assert.Equal(0m, v.PartOrganisme);
        Assert.True(v.ContientSousRegistre);
    }

    [Fact]
    public void Quantite_limitee_au_stock_du_lot_lignes_cumulees()
    {
        var v = new Vente();
        var lot = LotDe(Amoxicilline, quantite: 3);
        v.AjouterProduit(Amoxicilline, lot, 2, Aujourdhui);
        var ex = Assert.Throws<RegleMetierException>(() => v.AjouterProduit(Amoxicilline, lot, 2, Aujourdhui));
        Assert.Contains("3 boîte(s) disponible(s), 4 demandée(s)", ex.Message);
    }

    [Theory]
    [InlineData(0, false)]    // périmé le jour même
    [InlineData(100, true)]   // bloqué
    public void Lot_non_vendable_refuse(int joursRestants, bool bloque) =>
        Assert.Throws<RegleMetierException>(() =>
            new Vente().AjouterProduit(Amoxicilline, LotDe(Amoxicilline, joursRestants: joursRestants, bloque: bloque), 1, Aujourdhui));

    [Fact]
    public void Remise_sur_la_part_du_patient_seulement()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80 };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 2, Aujourdhui);
        v.AppliquerRemise(10, remiseMaxPct: 10);
        Assert.Equal(22m, v.MontantRemise);           // 10 % de 220
        Assert.Equal(480m, v.PartOrganisme);          // inchangée
        Assert.Equal(198m, v.PartPatient);
        Assert.Equal(678m, v.MontantTotal);
    }

    [Fact]
    public void Remise_au_dela_du_maximum_du_role_refusee() =>
        Assert.Throws<RegleMetierException>(() => new Vente().AppliquerRemise(15, remiseMaxPct: 10));

    [Fact]
    public void Tiers_payant_exige_assure_organisme_et_taux()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80, OrganismeId = 1 };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);
        Assert.Throws<RegleMetierException>(() => v.VerifierAvantEncaissement(ordonnanceSaisie: false));
    }

    [Fact]
    public void Psychotrope_exige_ordonnance_puis_patient()
    {
        var sansPatient = new Vente();
        sansPatient.AjouterProduit(Alprazolam, LotDe(Alprazolam), 1, Aujourdhui);
        Assert.Contains("ordonnance", Assert.Throws<RegleMetierException>(() => sansPatient.VerifierAvantEncaissement(false)).Message);
        Assert.Contains("patient", Assert.Throws<RegleMetierException>(() => sansPatient.VerifierAvantEncaissement(true)).Message);

        var complet = new Vente { PatientId = 5 };
        complet.AjouterProduit(Alprazolam, LotDe(Alprazolam), 1, Aujourdhui);
        complet.VerifierAvantEncaissement(true);
    }

    [Fact]
    public void Panier_vide_refuse() =>
        Assert.Throws<RegleMetierException>(() => new Vente().VerifierAvantEncaissement(false));

    [Fact]
    public void Especes_rendu_de_monnaie_et_montant_insuffisant()
    {
        var v = new Vente();
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);
        Assert.Equal(150m, Reglement.Pour(v, ModePaiement.Especes, 500m).Rendu);
        Assert.Equal(0m, Reglement.Pour(v, ModePaiement.Especes, null).Rendu);   // appoint exact
        Assert.Throws<RegleMetierException>(() => Reglement.Pour(v, ModePaiement.Especes, 300m));
        Assert.Equal(0m, Reglement.Pour(v, ModePaiement.Carte, null).Rendu);
    }

    [Fact]
    public void Comptage_par_coupure()
    {
        var c = Comptage.Depuis(new Dictionary<string, decimal> { ["b2000"] = 2, ["b200"] = 3, ["autres"] = 45 });
        Assert.Equal(4645m, c.Total);
        Assert.Equal(6, c.Lignes.Count);
    }

    [Theory]
    [InlineData("b500", -1)]
    [InlineData("b1000", 1.5)]
    public void Comptage_invalide_refuse(string coupure, double valeur) =>
        Assert.Throws<RegleMetierException>(() => Comptage.Depuis(new Dictionary<string, decimal> { [coupure] = (decimal)valeur }));

    [Fact]
    public void Cloture_calcule_attendu_et_ecart()
    {
        var session = new SessionCaisse { FondCaisse = 5000m };
        var r = session.Cloturer(Comptage.Depuis(new Dictionary<string, decimal> { ["b2000"] = 24, ["b500"] = 1 }), ventesEspeces: 45000m, seuilEcart: 500m);
        Assert.Equal(50000m, r.Attendu);
        Assert.Equal(48500m, r.Compte);
        Assert.Equal(-1500m, r.Ecart);
        Assert.True(r.AuDelaDuSeuil);
    }

    [Fact]
    public void Session_deja_cloturee_refusee() =>
        Assert.Throws<RegleMetierException>(() =>
            new SessionCaisse { FermeeLe = DateTime.UtcNow }.Cloturer(Comptage.Depuis(new Dictionary<string, decimal>()), 0, 500));
}
