using Pharma.Application.Caisse;
using Pharma.Application.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Rapprochement d'un volet après un retour (décision du titulaire du 2026-11-21 : ajuster, script 157) : comparaison sur les boîtes
/// servies nettes des retours, régularisation sur les seules boîtes gardées, part déjà rendue transmise pour n'être déduite qu'une fois.
/// </summary>
public class RapprochementApresRetourTests
{
    // Volet corrigé : 3 boîtes de VIT D3 à 156,48 DA ; volet non corrigé : 5 boîtes
    private const string VoletCorrige = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;";
    private const string VoletNonCorrige = "06930127;11/11/2026;690755003842;80;24;2;09/11/2026;07607,5,156.48;";
    private static readonly DateTime Maintenant = CatalogueEnMemoire.Aujourdhui.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);

    /// <summary>5 boîtes vendues à <paramref name="prix"/>, 80 %, dont 2 rendues avant le volet.</summary>
    private static VenteAttenteVolet Vente(decimal prix, decimal part, decimal organismeRendu, decimal patientRendu) =>
        new(2, "T-000002", Maintenant.AddHours(-1), 12, "Benali Amine", "690755003842/80", "CNAS", 80, prix * 5, part, "Vendeur A",
            [new LigneVenteRapprochee(18, "VIT D3", 5, prix, 2)], PartPatient: prix * 5 - part, OrganismeRendu: organismeRendu,
            PatientRendu: patientRendu, BoitesRendues: 2);

    private static (RapprocherVoletChifa Cas, RapprochementsEnMemoire Ventes) Cas(params string[] droits)
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(18, "VIT D3", 160m);
        var ventes = new RapprochementsEnMemoire();
        return (new RapprocherVoletChifa(volets, ventes, new SessionsEnMemoire(), new DroitsFactices(droits.Length == 0 ? ["caisse.vendre"] : droits),
            new JournalEspion(), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui)), ventes);
    }

    [Fact]
    public async Task Volet_corrige_apres_retour_concorde_et_ecart_sur_les_seules_boites_gardees()
    {
        // 5 × 160 (part caisse 640) ; 2 rendues : 256 retirés ; part retenue 384 face à la part Chifa des 3 boîtes gardées 375,55
        var (cas, ventes) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(160m, 640m, 256m, 64m));
        var p = await cas.ProposerAsync(VoletCorrige, 5);
        Assert.True(p.Candidates[0].Comparaison.Concorde);   // 3 sur le volet, 3 servies nettes
        var r = await cas.RattacherAsync(VoletCorrige, 2, null, 5, RegularisationPartOrganisme.PertePharmacie);
        Assert.False(r.AvecEcart);
        Assert.Equal(8.45m, r.EcartPartOrganisme);           // jamais 8,45 + 256 : le retour n'est pas déduit une seconde fois
        var rat = Assert.Single(ventes.Rattaches);
        Assert.Equal((375.55m, 256m, 2), (rat.Regularisation!.PartOrganismeChifa, rat.OrganismeRenduAvant, rat.BoitesRendues));
    }

    [Fact]
    public async Task Volet_non_corrige_apres_retour_ecart_de_quantite_sans_regularisation()
    {
        // 5 × 156,48 (part caisse 625,92) ; 2 rendues : 250,37 retirés ; part Chifa des 3 boîtes gardées 375,55 = part retenue
        var (cas, ventes) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(156.48m, 625.92m, 250.37m, 62.59m));
        var c = (await cas.ProposerAsync(VoletNonCorrige, 5)).Candidates[0].Comparaison;
        Assert.Equal(RapprochementVolet.Quantite, Assert.Single(c.Lignes).Ecart);
        Assert.Equal(0m, c.EcartPartOrganisme);
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => cas.RattacherAsync(VoletNonCorrige, 2, null, 5));   // motif exigé
        var r = await cas.RattacherAsync(VoletNonCorrige, 2, "Facture pas encore corrigée dans CHIFA-Officine", 5);
        Assert.True(r.AvecEcart);
        var rat = Assert.Single(ventes.Rattaches);
        Assert.Null(rat.Regularisation);
        Assert.Contains("après 2 rendue(s)", rat.DetailEcarts);
        Assert.Equal(250.37m, rat.OrganismeRenduAvant);
    }

    [Fact]
    public async Task Caisse_paiera_plus_apres_retour_rendu_au_patient_calcule_sur_les_boites_gardees()
    {
        // 5 × 150 (part caisse 600, patient 150) ; 2 rendues (240 et 60) ; gardées : total 450, patient 90, part 360 ; Chifa 375,55
        var (cas, ventes) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(150m, 600m, 240m, 60m));
        await cas.RattacherAsync(VoletCorrige, 2, "Prix Chifa supérieur", 5);
        var g = Assert.Single(ventes.Rattaches).Regularisation!;
        // Patient : 90 payés pour une part Chifa de 450 − 375,55 = 74,45 → 15,55 rendus (sur les ventes brutes : rien)
        Assert.Equal((RegularisationPartOrganisme.AvoirPatient, -15.55m, 15.55m), (g.Sort, g.Ecart, g.MontantPatient));
    }

    [Fact]
    public async Task Retour_apres_regularisation_faite_apres_un_premier_retour_prorata_de_la_part_chifa_et_solde_exact()
    {
        // Suite du premier cas : régularisation 375,55 (Chifa des 3 boîtes gardées) + 256 rendus avant = part retenue lue 631,55
        var vente = new VenteRetournable(
            new VenteAnnulable(2, "T-000002", DateTime.UtcNow, "Vendeur", "Patient Fictif", 800, 160, 640, "especes", "validee", false, false, [],
                PatientId: 12),
            [new LigneRetournable(1, 10, "VIT D3", "L1", 5, 2, 160, [], 640)],
            new OrganismeRetour(375.55m + 256m, 256m, "chifa", null, "06930126"));
        var (cas, stock) = Monter(vente);
        var r = await cas.ExecuterAsync(2, "Erreur", [new(1, 1, "stock", null)], 8, "Caisse 1", null, null);
        Assert.Equal(125.18m, r.PartOrganismeRetiree);   // part Chifa d'une boîte (156,48 × 80 %), pas celle de la caisse (128)
        Assert.Equal(125.18m, stock.Ecrit!.Lignes[0].MontantOrganisme);
        // Dernières boîtes : le solde exact 375,55 − 125,18
        var fin = vente with
        {
            Lignes = [vente.Lignes[0] with { DejaRendue = 3 }],
            Organisme = vente.Organisme! with { DejaRetiree = 256m + 125.18m }
        };
        var (cas2, _) = Monter(fin);
        Assert.Equal(250.37m, (await cas2.ExecuterAsync(2, "Erreur", [new(1, 2, "stock", null)], 8, "Caisse 1", null, null)).PartOrganismeRetiree);
    }

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock) Monter(VenteRetournable v)
    {
        var stock = new RetoursEnMemoire(v);
        var codes = new CodesEnMemoire();
        var droits = new DroitsParEmploye(new() { [8] = ["caisse.vendre", "caisse.retour_especes"] });
        var journal = new JournalEspion();
        return (new EnregistrerRetourClient(stock, new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 9)), journal), journal), stock);
    }
}
