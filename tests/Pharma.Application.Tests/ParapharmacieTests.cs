using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Catégories et classements de produits en mémoire : medicament, hygiene et parapharmacie (non médicaments).</summary>
internal sealed class ClassementEnMemoire : IClassementProduits
{
    public Dictionary<long, ClassementProduit> Produits { get; } = new()
    {
        [1] = new(1, "Crème mains", "libre", "medicament", null),
        [2] = new(2, "Gel douche", "libre", "medicament", null),
        [3] = new(3, "Doliprane 500 mg", "libre", "medicament", null, DansNomenclature: true),
        [4] = new(4, "Sirop liste II", "liste_ii", "medicament", null),
        [5] = new(5, "Produit DCI surveillée", "libre", "medicament", null, SubstanceSurveillee: true),
        [6] = new(6, "Savon déjà classé", "libre", "hygiene", null)
    };
    public List<(long Id, string Categorie)> Modifies { get; } = [];

    public Task<IReadOnlyList<CategorieProduit>> CategoriesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CategorieProduit>>([new("medicament", "Médicament", true), new("hygiene", "Hygiène", false),
            new("parapharmacie", "Parapharmacie (à préciser)", false)]);
    public Task<ClassementProduit?> LireAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult(Produits.TryGetValue(produitId, out var p) ? p : null);
    public Task<bool> ModifierAsync(long produitId, string categorie, string? listePsychotrope, CancellationToken ct = default)
    {
        Modifies.Add((produitId, categorie));
        Produits[produitId] = Produits[produitId] with { Categorie = categorie };
        return Task.FromResult(true);
    }
    public Task<Page<ProduitAClasser>> AClasserAsync(string? texte, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new Page<ProduitAClasser>([], 0, page.Page, page.Taille));
}

public class ParapharmacieApplicationTests
{
    // ---- Reclassement groupé ----

    [Fact]
    public async Task Reclassement_groupe_une_ligne_de_journal_par_produit()
    {
        var (c, journal) = (new ClassementEnMemoire(), new JournalEspion());
        var n = await new ReclasserProduits(c, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync([1, 2, 6, 1], "parapharmacie", 3);
        Assert.Equal(3, n);                                                    // 1 compté une fois ; 6 change d'hygiène à parapharmacie
        Assert.Equal([(1L, "parapharmacie"), (2L, "parapharmacie"), (6L, "parapharmacie")], c.Modifies);
        Assert.Equal(["produit_classe", "produit_classe", "produit_classe"], journal.Actions);
        Assert.Equal("medicament", journal.Details[0]["categorie_avant"]);
        Assert.Equal("parapharmacie", journal.Details[0]["categorie"]);
        Assert.Equal(true, journal.Details[0]["reclassement_groupe"]);
        Assert.Equal("hygiene", journal.Details[2]["categorie_avant"]);
    }

    [Fact]
    public async Task Reclassement_groupe_sans_le_droit_refuse()
    {
        var c = new ClassementEnMemoire();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ReclasserProduits(c, new DroitsFactices("stock.consulter"), new JournalEspion()).ExecuterAsync([1], "parapharmacie", 3));
        Assert.Empty(c.Modifies);
    }

    [Fact]
    public async Task Reclassement_groupe_code_inconnu_ou_aucun_produit_refuse()
    {
        var cas = new ReclasserProduits(new ClassementEnMemoire(), new DroitsFactices("catalogue.creer"), new JournalEspion());
        Assert.Equal("categorie", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync([1], "cosmetique", 3))).Champ);
        Assert.Equal("produits", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync([], "parapharmacie", 3))).Champ);
        Assert.Equal("produits", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(Enumerable.Range(1, ReclasserProduits.Maximum + 1).Select(i => (long)i).ToList(), "parapharmacie", 3))).Champ);
    }

    [Theory]
    [InlineData(3L, "nomenclature nationale")]
    [InlineData(4L, "hors liste")]
    [InlineData(5L, "substance surveillée")]
    public async Task Reclassement_groupe_tout_ou_rien_produit_refuse_nomme(long refuse, string motif)
    {
        var (c, journal) = (new ClassementEnMemoire(), new JournalEspion());
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ReclasserProduits(c, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync([1, refuse], "parapharmacie", 3));
        Assert.Contains($"« {c.Produits[refuse].Nom} »", e.Message);
        Assert.Contains(motif, e.Message);
        Assert.Empty(c.Modifies);                                              // rien n'est écrit, pas même le produit 1
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Retour_en_medicament_toujours_permis()
    {
        var c = new ClassementEnMemoire();
        Assert.Equal(1, await new ReclasserProduits(c, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync([6], "medicament", 3));
    }

    [Fact]
    public async Task Classement_un_a_un_refuse_la_parapharmacie_pour_un_produit_de_la_nomenclature()
    {
        var c = new ClassementEnMemoire();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierClassementProduit(c, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(3, "hygiene", null, 3));
        Assert.Equal("categorie", e.Champ);
        Assert.Empty(c.Modifies);
    }

    // ---- Création ----

    private static FicheProduit Para(string categorie = "parapharmacie", string? num = null, ClasseReglementaire classe = ClasseReglementaire.Libre, string dci = "") =>
        new("Crème mains 50 ml", dci, "", "", 450m, classe, null, null, false, 19m, null, num, categorie);

    [Fact]
    public async Task Creation_parapharmacie_sans_DCI_ni_dosage_ni_forme()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer", "prix.modifier"), journal).ExecuterAsync(Para(), 2);
        var cree = catalogue.Crees.Single();
        Assert.Equal(("parapharmacie", "", 19m), (cree.Categorie, cree.Dci, cree.TauxTva));
        Assert.Null(journal.Details.Single()["dci"]);
    }

    [Fact]
    public async Task Creation_medicament_sans_DCI_toujours_refusee()
    {
        await Assert.ThrowsAsync<RegleMetierException>(() => new CreerProduit(new CatalogueEcritureEnMemoire(),
            new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion()).ExecuterAsync(Para("medicament"), 2));
    }

    [Theory]
    [InlineData("cosmetique", null, ClasseReglementaire.Libre, "", "inconnue")]
    [InlineData("parapharmacie", "99/00/C", ClasseReglementaire.Libre, "", "nomenclature nationale")]
    [InlineData("hygiene", null, ClasseReglementaire.ListeI, "", "hors liste")]
    [InlineData("parapharmacie", null, ClasseReglementaire.Libre, "Alprazolam", "substance surveillée")]
    public async Task Creation_parapharmacie_refusee(string categorie, string? num, ClasseReglementaire classe, string dci, string motif)
    {
        var catalogue = new CatalogueEcritureEnMemoire();
        // Titulaire compris : la règle ne dépend pas de qui crée
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => new CreerProduit(catalogue,
            new DroitsFactices("catalogue.creer", "prix.modifier") { Titulaire = true }, new JournalEspion()).ExecuterAsync(Para(categorie, num, classe, dci), 1));
        Assert.Contains(motif, e.Message);
        Assert.Equal("categorie", e.Champ);
        Assert.Empty(catalogue.Crees);
    }

    // ---- Modification ----

    [Fact]
    public async Task Modification_change_la_categorie_et_la_journalise()
    {
        var (p, journal) = (new ProduitsModifEnMemoire(), new JournalEspion());
        p.Fiche = p.Fiche with { Classe = "libre", Categorie = "medicament" };
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), journal).ExecuterAsync(p.Fiche with { Dci = null, Categorie = "parapharmacie" }, 3);
        Assert.Equal("parapharmacie", p.Modifiee!.Categorie);
        Assert.Contains("categorie", journal.Details.Single().Keys);
    }

    [Fact]
    public async Task Modification_categorie_absente_de_la_saisie_inchangee()
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Classe = "libre", Categorie = "parapharmacie", Dci = null };
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { Categorie = null, NomCommercial = "Autre nom" }, 3);
        Assert.Equal("parapharmacie", p.Modifiee!.Categorie);
    }

    [Theory]
    [InlineData("liste_i", false, "hors liste")]
    [InlineData("libre", true, "nomenclature nationale")]
    public async Task Modification_parapharmacie_refusee(string classe, bool dansNomenclature, string motif)
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Classe = classe, Categorie = "medicament", DansNomenclature = dansNomenclature };
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion()).ExecuterAsync(p.Fiche with { Categorie = "parapharmacie" }, 1));
        Assert.Contains(motif, e.Message);
        Assert.Null(p.Modifiee);
    }

    [Fact]
    public async Task Modification_categorie_inconnue_refusee()
    {
        var p = new ProduitsModifEnMemoire();
        p.Fiche = p.Fiche with { Classe = "libre", Categorie = "medicament" };
        Assert.Equal("categorie", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { Categorie = "inconnue" }, 3))).Champ);
    }

    // ---- Stock › Produits : pastille « Parapharmacie » ----

    [Fact]
    public async Task Filtre_parapharmacie_du_stock()
    {
        static LigneStock L(long id, string nom, bool para) => new(id, nom, null, null, ClasseReglementaire.Libre, 5, 30, null, 1, ["G"], ["L1"], "ok", false,
            Categorie: para ? "hygiene" : "medicament", CategorieLibelle: para ? "Hygiène" : "Médicament", Parapharmacie: para);
        var cas = new ConsulterStock(new StockLectureEnMemoire([L(1, "Doliprane", false), L(2, "Savon", true), L(3, "Shampooing", true)]));
        var p = await cas.PageAsync(new FiltreStock(Filtre: "parapharmacie"), new DemandePage());
        Assert.Equal(2, p.Page.Total);
        Assert.All(p.Page.Lignes, l => Assert.True(l.Parapharmacie));
        Assert.Equal(3, (await cas.PageAsync(new FiltreStock(Filtre: "tous"), new DemandePage())).Page.Total);
    }
}
