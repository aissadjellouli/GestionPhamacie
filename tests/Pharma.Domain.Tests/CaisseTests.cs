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
        v.SaisirResteACharge(320m);
        Assert.Equal((320m, 330m, 650m), (v.PartPatient, v.PartOrganisme, v.MontantTotal));
        Assert.Equal(330m, v.Lignes[0].MontantOrganisme);                     // tout sur la ligne remboursable (pas de plafond au tarif de 300)
        Assert.Equal(0m, v.Lignes[1].MontantOrganisme);                       // non remboursable : rien pour l'organisme
        v.VerifierAvantEncaissement(ordonnanceSaisie: false);                  // taux non exigé quand le reste est saisi
    }

    [Fact]
    public void Reste_a_charge_au_moins_le_montant_des_produits_non_remboursables()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, PatientId = 1, OrganismeId = 1 };
        var autre = new Produit { Id = 5, NomCommercial = "B", PrixPublic = 300m, Remboursable = false };
        var sansTarif = new Produit { Id = 6, NomCommercial = "C", PrixPublic = 100m, Remboursable = true, TarifReference = null };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 2, Aujourdhui);  // 700, pris en charge
        v.AjouterProduit(autre, LotDe(autre), 1, Aujourdhui);                 // 300, non remboursable
        v.AjouterProduit(sansTarif, LotDe(sansTarif), 1, Aujourdhui);         // 100, sans tarif de référence
        var ex = Assert.Throws<RegleMetierException>(() => v.SaisirResteACharge(399.99m));
        Assert.Contains("non remboursables", ex.Message);
        Assert.Equal("reste_a_charge", ex.Champ);
        v.SaisirResteACharge(400m);
        Assert.Equal((400m, 700m), (v.PartPatient, v.PartOrganisme));
        Assert.Equal([700m, 0m, 0m], v.Lignes.Select(l => l.MontantOrganisme));
    }

    [Fact]
    public void Reste_a_charge_saisi_reparti_sur_les_lignes_prises_en_charge_et_reductions_gardees()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, PatientId = 1, OrganismeId = 1 };
        var autreRemb = new Produit { Id = 8, NomCommercial = "D", PrixPublic = 650m, Remboursable = true, TarifReference = 600m };
        var creme = new Produit { Id = 9, NomCommercial = "Crème", PrixPublic = 200m, Remboursable = false, EstMedicament = false };
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);   // 350
        v.AjouterProduit(autreRemb, LotDe(autreRemb), 1, Aujourdhui);         // 650
        v.AjouterProduit(creme, LotDe(creme), 1, Aujourdhui);                 // 200
        v.SaisirResteACharge(300m);                                            // organisme : 900 sur 1 000 pris en charge
        Assert.Equal([315m, 585m, 0m], v.Lignes.Select(l => l.MontantOrganisme));
        v.AppliquerRemise(10);                                                  // remise du vendeur sur la part du patient de chaque ligne
        Assert.Equal(20m, v.Lignes[2].Reduction);                              // crème : 10 % de 200 (part du patient entière)
        Assert.Equal(900m, v.PartOrganisme);
        Assert.Equal(300m - v.MontantRemise - v.MontantDon, v.PartPatient);
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
    public void Lot_d_un_autre_produit_refuse_avec_un_message_clair()
    {
        var ex = Assert.Throws<RegleMetierException>(() => new Vente().AjouterProduit(Amoxicilline, LotDe(Alprazolam), 1, Aujourdhui));
        Assert.Equal("Amoxicilline 1 g : ce lot n’appartient pas à ce produit, rescannez la boîte.", ex.Message);
    }

    [Fact]
    public void Produit_bloque_a_la_vente_refuse_avec_son_motif()
    {
        var bloque = new Produit { Id = 9, NomCommercial = "Bloqué", PrixPublic = 100m, BloqueVente = true, MotifBlocage = "rappel du fabricant" };
        var ex = Assert.Throws<RegleMetierException>(() => new Vente().AjouterProduit(bloque, LotDe(bloque), 1, Aujourdhui));
        Assert.Contains("rappel du fabricant", ex.Message);
    }

    [Fact]
    public void Remise_hors_SHP()
    {
        // 352,50 dont 2,50 de SHP : 10 % de remise sur 350 seulement
        var produit = new Produit { Id = 3, NomCommercial = "P", PrixPublic = 350m, Classe = ClasseReglementaire.ListeI, EstMedicament = false };
        var v = new Vente();
        v.AjouterProduit(produit, LotDe(produit), 1, Aujourdhui);
        v.AppliquerRemise(10);
        Assert.Equal((35m, 317.50m, 0m), (v.MontantRemise, v.PartPatient, v.MontantDon));
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
        v.AppliquerRemise(10);
        // Médicament : la réduction est un don partiel du reste à charge, pas une remise
        Assert.Equal((0m, 22m), (v.MontantRemise, v.MontantDon));   // 10 % de 220
        Assert.Equal(480m, v.PartOrganisme);          // inchangée
        Assert.Equal(198m, v.PartPatient);
        Assert.Equal(678m, v.MontantTotal);
        Assert.True(v.DonManuelSurMedicaments);
        Assert.False(v.RemiseManuelleSurProduits);
    }

    [Theory]
    [InlineData(101)]
    [InlineData(-1)]
    [InlineData(12.5)]
    public void Remise_nombre_entier_de_0_a_100(decimal pct) =>
        Assert.Throws<RegleMetierException>(() => new Vente().AppliquerRemise(pct));

    [Fact]
    public void Remise_negative_refusee_avec_un_message_clair()
    {
        var ex = Assert.Throws<RegleMetierException>(() => new Vente().AppliquerRemise(-1));
        Assert.Equal("La remise ne peut pas être négative : saisissez un nombre entier de 0 à 100 %.", ex.Message);
        Assert.Equal("remise", ex.Champ);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public void Remise_de_0_et_de_100_acceptee(decimal pct) => new Vente().AppliquerRemise(pct);

    private static readonly Produit Creme = new() { Id = 11, NomCommercial = "Crème", PrixPublic = 1000m, EstMedicament = false, Categorie = "dermo_cosmetique" };

    [Fact]
    public void Remise_automatique_en_vente_libre_puis_remise_du_vendeur_additionnee_ou_non()
    {
        var v = new Vente();
        v.AjouterProduit(Creme, LotDe(Creme), 1, Aujourdhui);
        v.AjouterProduit(Amoxicilline, LotDe(Amoxicilline), 1, Aujourdhui);   // remboursable : jamais de remise automatique
        v.AppliquerRemisesAutomatiques(new Dictionary<long, decimal> { [11] = 10, [Amoxicilline.Id] = 20 });
        Assert.Equal((10m, 0m), (v.Lignes[0].RemiseAutoPct, v.Lignes[1].RemiseAutoPct));
        Assert.Equal(100m, v.MontantRemise);

        // Remise du vendeur non additionnée : seulement sur la ligne sans remise automatique (don partiel : médicament)
        v.AppliquerRemise(5);
        Assert.Equal((100m, 17.50m), (v.MontantRemise, v.MontantDon));
        Assert.False(v.RemiseManuelleSurProduits);

        // Additionnée : 10 + 5 = 15 % sur la crème
        v.AppliquerRemise(5, additionner: true);
        Assert.Equal((150m, 17.50m, 100m), (v.MontantRemise, v.MontantDon, v.MontantReductionsAuto));
        Assert.True(v.RemiseManuelleSurProduits);
        Assert.Equal(1350m - 150m - 17.50m, v.PartPatient);
    }

    [Fact]
    public void Pas_de_remise_automatique_en_tiers_payant()
    {
        var v = new Vente { Type = TypeVente.TiersPayant, TauxTiersPayant = 80 };
        v.AjouterProduit(Creme, LotDe(Creme), 1, Aujourdhui);
        v.AppliquerRemisesAutomatiques(new Dictionary<long, decimal> { [11] = 10 });
        Assert.Equal(0m, v.MontantRemise);
    }

    [Fact]
    public void Remises_additionnees_au_plus_100_pour_cent()
    {
        var v = new Vente();
        v.AjouterProduit(Creme, LotDe(Creme), 1, Aujourdhui);
        v.AppliquerRemisesAutomatiques(new Dictionary<long, decimal> { [11] = 70 });
        v.AppliquerRemise(50, additionner: true);
        Assert.Equal((100m, 1000m, 0m), (v.Lignes[0].PctApplique, v.MontantRemise, v.PartPatient));
    }

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
    public void Excedent_au_dela_du_seuil_aussi_signale()
    {
        var session = new SessionCaisse { FondCaisse = 5000m };
        var r = session.Cloturer(Comptage.Depuis(new Dictionary<string, decimal> { ["b2000"] = 3, ["b1000"] = 2, ["p100"] = 9 }), ventesEspeces: 2692.50m, seuilEcart: 500m);
        Assert.Equal(1207.50m, r.Ecart);
        Assert.True(r.AuDelaDuSeuil);
        Assert.Equal("Excédent", NatureEcart.Libelle(r.Ecart));
        Assert.False(NatureEcart.EstManque(r.Ecart));
    }

    [Theory]
    [InlineData(-0.5, "Manque")]
    [InlineData(0, "Écart")]
    [InlineData(300, "Excédent")]
    public void Nature_de_l_ecart(double ecart, string attendu) => Assert.Equal(attendu, NatureEcart.Libelle((decimal)ecart));

    [Fact]
    public void Session_deja_cloturee_refusee() =>
        Assert.Throws<RegleMetierException>(() =>
            new SessionCaisse { FermeeLe = DateTime.UtcNow }.Cloturer(Comptage.Depuis(new Dictionary<string, decimal>()), 0, 500));
}
