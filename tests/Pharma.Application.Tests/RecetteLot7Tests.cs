using Pharma.Application.Caisse;
using Pharma.Application.Catalogue;
using Pharma.Application.Notifications;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Tests;

public class RecetteLot7Tests
{
    // ---- Alerte d'écart de caisse : celui qui a compté la caisse, pas celui qui l'a ouverte ----

    [Fact]
    public async Task Alerte_d_ecart_nomme_celui_qui_cloture_et_non_celui_qui_a_ouvert()
    {
        // Caisse ouverte par l'employé 5 (« Vendeur A »), clôturée par l'employé 9 avec un manque de 2 000 DA
        var sessions = new SessionsEnMemoire { Especes = 1000 };
        var (journal, alertes) = (new JournalEspion(), new AlertesTitulaireEspion());
        await new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.vendre"), journal, alertes)
            .ExecuterAsync(9, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 2 });

        Assert.Equal(("Employé 9", "Caisse 1", -2000m, false), alertes.Ecarts.Single());
        Assert.Equal(["ecart_caisse"], journal.Actions);
        Assert.False(journal.Details.Single().ContainsKey("releve"));
    }

    [Fact]
    public async Task Ecart_d_une_releve_dit_comme_tel_au_journal_et_dans_l_alerte()
    {
        var sessions = new SessionsEnMemoire { Especes = 1000 };
        var (journal, alertes) = (new JournalEspion(), new AlertesTitulaireEspion());
        var cloturer = new CloturerSessionCaisse(sessions, new DroitsFactices("caisse.vendre"), journal, alertes);
        await new ReleverCaisse(sessions, cloturer, journal).ExecuterAsync(9, "Caisse 1", new Dictionary<string, decimal> { ["b2000"] = 4 });

        Assert.Equal(("Employé 9", "Caisse 1", 2000m, true), alertes.Ecarts.Single());
        Assert.Equal(true, journal.Details[journal.Actions.IndexOf("ecart_caisse")]["releve"]);
    }

    [Fact]
    public async Task Texte_de_l_alerte_cloture_ou_releve()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { new Destinataire(1, "Titulaire", "t@p.dz", "0550000000", true, true,
            new HashSet<TypeNotification> { TypeNotification.EcartCaisse }, true, true) } };
        var alertes = new AlertesTitulaire(depot, new NotificationService(depot));

        await alertes.EcartCaisseAsync("Caissier Recette", "Caisse 1", -1200m, 500m);
        var cloture = depot.Ajoutes.Single(m => m.Canal == CanalNotification.Email);
        Assert.Contains("Clôturée par : Caissier Recette", cloture.Corps);
        Assert.Contains("à la clôture", cloture.Corps);

        depot.Ajoutes.Clear();
        await alertes.EcartCaisseAsync("Caissier Recette", "Caisse 1", -1200m, 500m, releve: true);
        var releve = depot.Ajoutes.Single(m => m.Canal == CanalNotification.Email);
        Assert.Contains("Relève faite par : Caissier Recette", releve.Corps);
        Assert.Contains("à la relève", releve.Corps);
        Assert.Contains(depot.Ajoutes, m => m.Canal == CanalNotification.Sms && m.Corps.Contains("a la releve de Caissier Recette"));
    }

    // ---- Classe la plus stricte : un psychotrope dont la substance est un stupéfiant ----

    [Theory]
    [InlineData(ClasseReglementaire.Psychotrope, "stupefiant", false, ClasseReglementaire.Stupefiant)]
    [InlineData(ClasseReglementaire.Libre, "stupefiant", false, ClasseReglementaire.Stupefiant)]
    [InlineData(ClasseReglementaire.ListeI, "psychotrope", false, ClasseReglementaire.Psychotrope)]
    [InlineData(ClasseReglementaire.Psychotrope, "psychotrope", false, null)]
    [InlineData(ClasseReglementaire.Stupefiant, "psychotrope", false, null)]
    [InlineData(ClasseReglementaire.Psychotrope, "stupefiant", true, null)]   // le titulaire décide
    [InlineData(ClasseReglementaire.Libre, null, false, null)]
    public void Classe_imposee_la_plus_stricte(ClasseReglementaire choisie, string? surveillee, bool titulaire, ClasseReglementaire? attendue) =>
        Assert.Equal(attendue, RegleProduit.ClasseImposee(choisie, surveillee, titulaire));

    private static FicheProduit Morphine(ClasseReglementaire classe, string dci = "Sulfate de morphine", string? num = null) =>
        new("Skenan 10 mg gél", dci, "10 mg", "gélule", 450m, classe, null, null, false, 0m, null, num);

    [Fact]
    public async Task Stupefiant_cree_en_psychotrope_par_un_employe_monte_en_stupefiant()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer"), journal)
            .ExecuterAsync(Morphine(ClasseReglementaire.Psychotrope) with { ListePsychotrope = "liste_i" }, 3);
        Assert.Equal((ClasseReglementaire.Stupefiant, true, null), (catalogue.Crees.Single().Classe, catalogue.AVerifier.Single(), catalogue.Crees.Single().ListePsychotrope));
        Assert.Contains("stupéfiant", cree.Information);
        Assert.Equal("psychotrope", journal.Details.Single()["classe_choisie"]);

        // Nomenclature stupéfiant, DCI anodine : idem
        var parNumero = new CatalogueEcritureEnMemoire();
        await new CreerProduit(parNumero, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(Morphine(ClasseReglementaire.Psychotrope, "Acénocoumarol", "56/78/B"), 3);
        Assert.Equal(ClasseReglementaire.Stupefiant, parNumero.Crees.Single().Classe);
    }

    [Fact]
    public async Task Stupefiant_modifie_par_un_employe_alors_qu_il_est_psychotrope_monte_en_stupefiant()
    {
        // Nomenclature : n° 12/34/A stupéfiant ; produit classé psychotrope
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Dci = "Alprazolam", Classe = "psychotrope", NumEnregistrement = "12/34/A" };
        var information = await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(p.Fiche with { NomCommercial = "Autre nom" }, 3);
        Assert.Equal(("stupefiant", true), (p.Modifiee!.Classe, p.ClasseImposee));
        Assert.Contains("n° d’enregistrement 12/34/A", information);

        // Le titulaire n'est pas concerné
        var t = new ProduitsModifEnMemoire();
        t.Fiche = p.Fiche with { Classe = "psychotrope" };
        Assert.Null(await new ModifierProduit(t, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(t.Fiche with { NomCommercial = "Autre nom" }, 1));
        Assert.Equal(("psychotrope", false), (t.Modifiee!.Classe, t.ClasseImposee));
    }

    // ---- Confirmation « hors registre » du titulaire mémorisée ----

    [Fact]
    public async Task Titulaire_qui_cree_un_produit_moins_strict_que_sa_substance_confirmation_memorisee_et_journalisee()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer") { Titulaire = true }, journal)
            .ExecuterAsync(Morphine(ClasseReglementaire.Libre), 1);
        Assert.Equal((ClasseReglementaire.Libre, false, (long?)1, null), (catalogue.Crees.Single().Classe, catalogue.AVerifier.Single(),
            catalogue.ConfirmeesPar.Single(), cree.Information));
        Assert.Equal(true, journal.Details.Single()["classe_confirmee"]);
        Assert.Equal("stupefiant", journal.Details.Single()["classe_proposee"]);

        // Classe cohérente avec la substance, ou DCI anodine : rien à confirmer
        var coherent = new CatalogueEcritureEnMemoire();
        await new CreerProduit(coherent, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(Morphine(ClasseReglementaire.Stupefiant), 1);
        var anodine = new CatalogueEcritureEnMemoire();
        await new CreerProduit(anodine, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(Morphine(ClasseReglementaire.Libre, "Paracétamol"), 1);
        Assert.Equal(((long?)null, (long?)null), (coherent.ConfirmeesPar.Single(), anodine.ConfirmeesPar.Single()));
    }

    [Fact]
    public async Task Classe_confirmee_par_le_titulaire_un_employe_ne_la_change_plus()
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Dci = "Alprazolam", Classe = "libre", ClasseConfirmee = true };
        var journal = new JournalEspion();
        Assert.Null(await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), journal)
            .ExecuterAsync(p.Fiche with { NomCommercial = "Autre nom", ClasseConfirmee = false }, 3));
        Assert.Equal(("libre", false, true), (p.Modifiee!.Classe, p.ClasseImposee, p.Modifiee.ClasseConfirmee));
        Assert.False(journal.Details.Single().ContainsKey("a_verifier"));

        // Même DCI écrite autrement : la confirmation vaut toujours
        Assert.Null(await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(p.Fiche with { Dci = "ALPRAZOLAM" }, 3));
    }

    [Fact]
    public async Task Dci_changee_par_un_employe_la_confirmation_tombe()
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Dci = "Alprazolam", Classe = "libre", ClasseConfirmee = true };
        var journal = new JournalEspion();
        var information = await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), journal)
            .ExecuterAsync(p.Fiche with { Dci = "Alprazolam (chlorhydrate)" }, 3);
        Assert.Equal(("psychotrope", true), (p.Modifiee!.Classe, p.ClasseImposee));
        Assert.Contains("invendable", information);
        Assert.Equal(true, journal.Details.Single()["confirmation_classe_retiree"]);
        Assert.Equal(true, journal.Details.Single()["a_verifier"]);
    }

    [Theory]
    [InlineData("libre", "psychotrope", true)]
    [InlineData("psychotrope", "stupefiant", true)]
    [InlineData("psychotrope", "psychotrope", false)]
    [InlineData("stupefiant", "psychotrope", false)]
    [InlineData("libre", null, false)]
    public async Task Classe_appliquee_par_le_titulaire_confirmation_memorisee_si_moins_stricte(string classe, string? surveillee, bool confirmation)
    {
        var stock = new NomenclatureEnMemoire { ClasseSurveillee = surveillee };
        var journal = new JournalEspion();
        await new AppliquerClasseNomenclature(stock, new DroitsFactices { Titulaire = true }, journal).ExecuterAsync(7, classe, 1);
        Assert.Equal(((7L, classe), confirmation), (stock.Changement!.Value, stock.Confirmation!.Value));
        Assert.Equal(confirmation, journal.Details.Single().ContainsKey("classe_confirmee"));
        if (confirmation) Assert.Equal(surveillee, journal.Details.Single()["classe_proposee"]);
    }
}
