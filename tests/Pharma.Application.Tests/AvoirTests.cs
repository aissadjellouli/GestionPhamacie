using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>
/// Avoir du patient en mémoire : le remboursement relit le solde sous un verrou (comme le verrou du patient en base), deux caisses ne
/// consomment donc jamais le même avoir.
/// </summary>
internal sealed class AvoirsEnMemoire(decimal solde) : IAvoirs
{
    private readonly SemaphoreSlim verrou = new(1, 1);
    public decimal Solde { get; private set; } = solde;
    public List<RemboursementAvoirAEcrire> Rembourses { get; } = [];

    public Task<decimal> SoldeAsync(long patientId, CancellationToken ct = default) => Task.FromResult(Solde);
    public Task<Page<MouvementAvoir>> MouvementsAsync(long patientId, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new Page<MouvementAvoir>([], 0, page.Page, page.Taille));

    public async Task<MouvementTiroirEcrit> RembourserAsync(RemboursementAvoirAEcrire r, CancellationToken ct = default)
    {
        await verrou.WaitAsync(ct);
        try
        {
            await Task.Delay(10, ct);   // les deux caisses ont lu le même solde avant d'écrire
            if (Solde < r.Montant) throw RegleAvoir.Insuffisant(Solde);
            Solde -= r.Montant;
            Rembourses.Add(r);
            return new MouvementTiroirEcrit(50 + Rembourses.Count, $"BS-0000{50 + Rembourses.Count}");
        }
        finally { verrou.Release(); }
    }
}

/// <summary>Avoir du patient (script 106, règles du titulaire 2026-11-15).</summary>
public class AvoirTests
{
    // Vente à crédit de 470 DA du patient 3 : il en a payé 300 (trop-payé), il doit encore 170 dessus
    private static VenteAnnulable VenteCredit(decimal paye = 300) =>
        new(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 470, 0, "credit", "validee", true, false,
            [new LigneVenteAnnulable("Produit fictif", 2, 470)], 0, 470 - paye, null, 5, 3, PayeSurVente: paye);

    private static (AnnulerVente Cas, AnnulationsEnMemoire Stock, JournalEspion Journal) Monter(VenteAnnulable v)
    {
        var stock = new AnnulationsEnMemoire(v);
        var journal = new JournalEspion();
        var cas = new AnnulerVente(stock, new SessionsEnMemoire { Especes = 10000, Parametres = new(true, 5000, 500, 0, SeuilSortieTiroir: 2000) },
            new DroitsParEmploye(new() { [5] = ["caisse.vendre"] }),
            new VerificationCodeComptoir(new CodesEnMemoire(), new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 15)), journal), journal);
        return (cas, stock, journal);
    }

    [Fact]
    public async Task Vente_a_credit_ordinaire_trois_choix_et_du_jamais_rembourse()
    {
        var (cas, stock, journal) = Monter(VenteCredit());
        stock.AutresDettes[30] = 80;
        // Choix obligatoire ; somme différente du trop-payé refusée ; le dû (170) n'est jamais rendu
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeRembourse: 470));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null,
            tropPayeImputations: [(30, 50)], tropPayeRembourse: 100, tropPayeAvoir: 200));
        Assert.Null(stock.Ecrite);

        var r = await cas.ExecuterAsync(12, "Erreur de saisie", 5, "Caisse 1", null, null,
            tropPayeImputations: [(30, 50)], tropPayeRembourse: 100, tropPayeAvoir: 150);
        Assert.Equal((100m, "especes", 50m, 150m), (r.ARendre, r.Mode, r.TropPayeImpute, r.AvoirCree));
        var e = stock.Ecrite!;
        Assert.Equal((170m, 100m, 150m), (e.ReductionDette, e.MontantRembourse, e.AvoirVente));
        Assert.Equal([(30L, 50m)], e.ImputationsVente!);
        var d = journal.Details.First(x => x.ContainsKey("avoir_cree"));
        Assert.Equal((300m, 150m), ((decimal)d["trop_paye"]!, (decimal)d["avoir_cree"]!));
    }

    [Fact]
    public async Task Avoir_cree_a_l_annulation_tout_le_trop_paye_laisse_en_avoir()
    {
        var (cas, stock, _) = Monter(VenteCredit());
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeAvoir: 300);
        Assert.Equal((0m, "credit", 300m), (r.ARendre, r.Mode, r.AvoirCree));
        Assert.Equal((300m, 0m), (stock.Ecrite!.AvoirVente, stock.Ecrite.MontantRembourse));
    }

    [Fact]
    public async Task Vente_sans_patient_aucun_avoir()
    {
        var v = VenteCredit() with { PatientId = null };
        var (cas, stock, _) = Monter(v);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeAvoir: 300));
        Assert.Null(stock.Ecrite);
    }

    [Fact]
    public async Task Regularisation_annulee_trop_paye_laisse_en_avoir_sans_bon_de_sortie()
    {
        var v = new VenteAnnulable(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 300, 170, "especes", "validee", true, false,
            [new LigneVenteAnnulable("Produit fictif", 1, 470)], 0, 0, null, 5, 3,
            Regularisation: new RegularisationVente(9, RegularisationPartOrganisme.ChargePatient, 100, 100));
        var (cas, stock, _) = Monter(v);
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null, tropPayeAvoir: 100);
        Assert.Equal((100m, 0m, 0m), (stock.Ecrite!.Regularisation!.Avoir, stock.Ecrite.Regularisation.Rembourse, stock.Ecrite.AvoirVente));
        Assert.Null(r.BonSortie);
        Assert.Equal(100m, r.AvoirCree);
    }

    [Fact]
    public async Task Vente_payee_en_partie_par_avoir_l_avoir_revient_a_l_avoir_le_reste_en_especes()
    {
        var v = new VenteAnnulable(12, "T-000012", DateTime.UtcNow, "Vendeur", "Patient fictif", 470, 470, 0, "especes", "validee", true, false,
            [new LigneVenteAnnulable("Produit fictif", 2, 470)], 0, 0, null, 5, 3, PayeParAvoir: 200);
        var (cas, stock, _) = Monter(v);
        var r = await cas.ExecuterAsync(12, "Erreur", 5, "Caisse 1", null, null);
        Assert.Equal((270m, 200m), (r.ARendre, r.AvoirCree));
        Assert.Equal((270m, 200m), (stock.Ecrite!.MontantRembourse, stock.Ecrite.AvoirVente));
    }

    [Fact]
    public void Repartition_somme_egale_au_trop_paye()
    {
        var autres = new Dictionary<long, decimal> { [30] = 80 };
        var r = RegularisationPartOrganisme.Repartir(300, [(30, 80)], 20, autres, 12, 200);
        Assert.Equal((80m, 20m, 200m), (r.Impute, r.Rembourse, r.Avoir));
        Assert.Throws<RegleMetierException>(() => RegularisationPartOrganisme.Repartir(300, null, 100, autres, 12, 100));
        Assert.Throws<RegleMetierException>(() => RegularisationPartOrganisme.Repartir(300, null, 0, autres, 12, -1));
        Assert.Throws<RegleMetierException>(() => RegularisationPartOrganisme.Repartir(300, null, 0, autres, 12, 300, patientConnu: false));
    }

    [Theory]
    [InlineData(50, 100, 80)]    // utilisation partielle
    [InlineData(80, 100, 80)]    // tout ce qui est à payer
    [InlineData(100, 100, 300)]  // tout l'avoir
    public void Utilisation_partielle_ou_totale_admise(decimal montant, decimal solde, decimal aPayer) =>
        RegleAvoir.VerifierUtilisation(montant, solde, aPayer, patientConnu: true);

    [Fact]
    public void Utilisation_refusee_au_dela_du_solde_ou_du_montant_ou_sans_patient()
    {
        var e = Assert.Throws<RegleMetierException>(() => RegleAvoir.VerifierUtilisation(150, 100, 300, true));
        Assert.Contains("Avoir insuffisant (solde : 100,00 DA)", e.Message.Replace(' ', ' ').Replace(' ', ' '));
        Assert.Throws<RegleMetierException>(() => RegleAvoir.VerifierUtilisation(90, 100, 80, true));
        Assert.Throws<RegleMetierException>(() => RegleAvoir.VerifierUtilisation(10, 100, 80, false));
    }

    [Fact]
    public async Task Dette_payee_avec_l_avoir_au_plus_son_solde()
    {
        var credits = new CreditsEnMemoire(500);
        var cas = new EncaisserReglementCredit(credits, new SessionsEnMemoire(), new JournalEspion(), new AvoirsEnMemoire(100));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(3, 150, RegleAvoir.Mode, 5, "Caisse 1"));
        Assert.Null(credits.Encaisse);
        await cas.ExecuterAsync(3, 100, RegleAvoir.Mode, 5, "Caisse 1");
        Assert.Equal((100m, "avoir"), (credits.Encaisse!.Value.Montant, credits.Encaisse.Value.Mode));
    }

    private static RembourserAvoir Rembourser(AvoirsEnMemoire avoirs, IPermissionService droits, decimal seuil = 2000, AutorisationCourante? autorisation = null) =>
        new(avoirs, new SessionsEnMemoire { Especes = 10000, Parametres = new(true, 5000, 500, 0, SeuilSortieTiroir: seuil) }, droits, new JournalEspion(), autorisation);

    [Fact]
    public async Task Concurrence_deux_caisses_ne_consomment_pas_le_meme_avoir()
    {
        var avoirs = new AvoirsEnMemoire(100);
        var droits = new DroitsFactices("caisse.vendre", Permissions.CaisseOuvrirTiroir);
        var a = Rembourser(avoirs, droits).ExecuterAsync(3, 80, "Demande du patient", 5, "Caisse 1");
        var b = Rembourser(avoirs, droits).ExecuterAsync(3, 80, "Demande du patient", 6, "Caisse 2");
        var resultats = await Task.WhenAll(Garder(a), Garder(b));
        Assert.Equal(1, resultats.Count(x => x is null));
        Assert.Contains(resultats, x => x is RegleMetierException);
        Assert.Equal(20m, avoirs.Solde);
        Assert.Single(avoirs.Rembourses);

        static async Task<Exception?> Garder(Task t) { try { await t; return null; } catch (Exception e) { return e; } }
    }

    [Fact]
    public async Task Remboursement_au_dela_du_seuil_exige_un_ayant_droit_ou_son_code()
    {
        // Seuil des sorties 50 DA : 80 DA à rendre
        var sansDroit = Rembourser(new AvoirsEnMemoire(100), new DroitsFactices("caisse.vendre", Permissions.CaisseOuvrirTiroir), 50);
        await Assert.ThrowsAsync<DroitRefuseException>(() => sansDroit.ExecuterAsync(3, 80, null, 5, "Caisse 1"));

        // Code comptoir d'un collègue qui a caisse.cloturer : il valide la sortie
        var avoirs = new AvoirsEnMemoire(100);
        var avecCode = Rembourser(avoirs, new DroitsFactices("caisse.vendre", Permissions.CaisseOuvrirTiroir, Permissions.CaisseCloturer), 50,
            new AutorisationCourante { AutoriseParId = 8, AutoriseParNom = "Assistante Fictive" });
        var bon = await avecCode.ExecuterAsync(3, 80, null, 5, "Caisse 1");
        Assert.StartsWith("BS-", bon.Numero);
        Assert.Equal(((long?)8, 20m), (avoirs.Rembourses.Single().ValidePar, avoirs.Solde));

        // Sous le seuil : la sortie se fait avec le seul droit d'ouvrir le tiroir ; au-delà du solde : refus
        var petit = new AvoirsEnMemoire(30);
        await Rembourser(petit, new DroitsFactices(Permissions.CaisseOuvrirTiroir), 50).ExecuterAsync(3, 30, null, 5, "Caisse 1");
        Assert.Equal((long?)null, petit.Rembourses.Single().ValidePar);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Rembourser(new AvoirsEnMemoire(30), new DroitsFactices(Permissions.CaisseOuvrirTiroir), 50).ExecuterAsync(3, 40, null, 5, "Caisse 1"));
        // Sans le droit d'ouvrir le tiroir : refus
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Rembourser(new AvoirsEnMemoire(30), new DroitsFactices("caisse.vendre"), 50).ExecuterAsync(3, 10, null, 5, "Caisse 1"));
    }
}
