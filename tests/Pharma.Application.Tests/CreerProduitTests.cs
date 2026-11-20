using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class CatalogueEcritureEnMemoire : ICatalogueEcriture
{
    public List<FicheProduit> Crees { get; } = [];
    public HashSet<string> Codes { get; } = ["6130000000107"];
    public Task<bool> CodeBarreExisteAsync(string code, CancellationToken ct = default) => Task.FromResult(Codes.Contains(code));
    public Task<string?> DoublonAsync(string nom, string dosage, string forme, CancellationToken ct = default) =>
        Task.FromResult<string?>(nom.Equals("paracétamol 500 mg cp", StringComparison.OrdinalIgnoreCase) && dosage == "500 mg" ? "Paracétamol 500 mg cp" : null);
    public List<bool> AVerifier { get; } = [];
    public List<long?> ConfirmeesPar { get; } = [];
    public Task<long> CreerAsync(FicheProduit f, bool aVerifier, CancellationToken ct = default, long? classeConfirmeePar = null)
    {
        Crees.Add(f); AVerifier.Add(aVerifier); ConfirmeesPar.Add(classeConfirmeePar);
        return Task.FromResult(50L);
    }
    public Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
    /// <summary>Substances surveillées : alprazolam (psychotrope), morphine (stupéfiant).</summary>
    public Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default) =>
        Task.FromResult<string?>(dci.Contains("alprazolam", StringComparison.OrdinalIgnoreCase) ? "psychotrope"
            : dci.Contains("morphine", StringComparison.OrdinalIgnoreCase) ? "stupefiant" : null);
    /// <summary>Nomenclature : n° 12/34/A (psychotrope), 56/78/B (stupéfiant).</summary>
    public Task<string?> ClasseNomenclatureAsync(string num, CancellationToken ct = default) =>
        Task.FromResult<string?>(num switch { "12/34/A" => "psychotrope", "56/78/B" => "stupefiant", _ => null });
    /// <summary>Catégories : medicament (médicament), parapharmacie et hygiene (non médicament) ; tout autre code est inconnu.</summary>
    public Task<bool?> CategorieMedicamentAsync(string code, CancellationToken ct = default) =>
        Task.FromResult<bool?>(code switch { "medicament" => true, "parapharmacie" or "hygiene" => false, _ => null });
    /// <summary>Nomenclature : 12/34/A, 56/78/B et 99/00/C (ce dernier hors registre).</summary>
    public Task<bool> DansNomenclatureAsync(string num, CancellationToken ct = default) => Task.FromResult(num is "12/34/A" or "56/78/B" or "99/00/C");
}

public class CreerProduitTests
{
    private static FicheProduit Fiche(string nom = "Sintrom 4 mg cp", string dosage = "4 mg", string? code = null) =>
        new(nom, "Acénocoumarol", dosage, "comprimé", 280m, ClasseReglementaire.ListeI, code, 280m, true, 0m, 5);

    [Fact]
    public async Task Creation_journalisee_avec_le_droit()
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer", "prix.modifier"), journal).ExecuterAsync(Fiche(), 2);
        Assert.Equal((50, null), (cree.Id, cree.Information));
        Assert.Equal("Sintrom 4 mg cp", catalogue.Crees.Single().NomCommercial);
        Assert.Equal(ClasseReglementaire.ListeI, catalogue.Crees.Single().Classe);
        Assert.False(catalogue.AVerifier.Single());
        Assert.Equal(["produit_cree"], journal.Actions);
    }

    // ---- Un seul droit pour toute saisie de prix : prix.modifier (décision du titulaire, 2026-11-15) ----

    [Theory]
    [InlineData(280, null, false)]
    [InlineData(0, 280, false)]
    [InlineData(0, 280, true)]
    public async Task Creation_avec_prix_tarif_ou_remboursable_refusee_sans_le_droit_de_modifier_un_prix(decimal prix, int? tarif, bool remboursable)
    {
        foreach (var droits in new[] { new DroitsFactices("catalogue.creer"), new DroitsFactices("catalogue.creer", "rapports.marges") { Titulaire = true } })
        {
            var catalogue = new CatalogueEcritureEnMemoire();
            var e = await Assert.ThrowsAsync<DroitRefuseException>(() => new CreerProduit(catalogue, droits, new JournalEspion())
                .ExecuterAsync(Fiche() with { PrixPublic = prix, TarifReference = tarif, Remboursable = remboursable }, 3));
            Assert.Equal("prix.modifier", e.Permission);   // autorisation sur place par code comptoir possible
            Assert.Empty(catalogue.Crees);
        }
    }

    [Fact]
    public async Task Creation_avec_prix_acceptee_avec_le_droit_de_modifier_un_prix()
    {
        var catalogue = new CatalogueEcritureEnMemoire();
        await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion()).ExecuterAsync(Fiche(), 3);
        Assert.Equal((280m, 280m, true), (catalogue.Crees.Single().PrixPublic, catalogue.Crees.Single().TarifReference, catalogue.Crees.Single().Remboursable));
    }

    [Fact]
    public async Task Creation_sans_prix_acceptee_sans_le_droit_le_produit_reste_sans_prix()
    {
        var catalogue = new CatalogueEcritureEnMemoire();
        await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer"), new JournalEspion())
            .ExecuterAsync(Fiche() with { PrixPublic = 0, TarifReference = null, Remboursable = false }, 3);
        Assert.Equal(0m, catalogue.Crees.Single().PrixPublic);
    }

    [Fact]
    public async Task Prix_negatif_toujours_refuse() =>
        await Assert.ThrowsAsync<RegleMetierException>(() => new CreerProduit(new CatalogueEcritureEnMemoire(),
            new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion()).ExecuterAsync(Fiche() with { PrixPublic = -1 }, 3));

    private static FicheProduit Psychotrope(ClasseReglementaire classe, string dci = "Alprazolam") =>
        new("Xanax 0,25 mg cp", dci, "0,25 mg", "comprimé", 190m, classe, null, null, false, 0m, null);

    [Theory]
    [InlineData("Alprazolam", ClasseReglementaire.Psychotrope)]
    [InlineData("Sulfate de MORPHINE", ClasseReglementaire.Stupefiant)]
    public async Task Psychotrope_cree_en_classe_libre_par_un_employe_classe_d_office_et_a_verifier(string dci, ClasseReglementaire attendue)
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer", "prix.modifier"), journal)
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre, dci), 3);
        Assert.Equal(attendue, catalogue.Crees.Single().Classe);
        Assert.True(catalogue.AVerifier.Single());
        Assert.Contains("invendable jusqu’à ce que le titulaire confirme sa classe", cree.Information);
        Assert.Equal(true, journal.Details.Single()["a_verifier"]);
        Assert.Equal("libre", journal.Details.Single()["classe_choisie"]);
    }

    [Theory]
    [InlineData("Acénocoumarol", "12/34/A", ClasseReglementaire.Psychotrope)]   // DCI anodine : la nomenclature suffit
    [InlineData("Alprazolam", "56/78/B", ClasseReglementaire.Stupefiant)]       // DCI psychotrope, nomenclature stupéfiant : la plus stricte
    public async Task Classe_proposee_par_la_nomenclature_pour_le_numero_d_enregistrement_imposee_a_un_employe(string dci, string num,
        ClasseReglementaire attendue)
    {
        var (catalogue, journal) = (new CatalogueEcritureEnMemoire(), new JournalEspion());
        var cree = await new CreerProduit(catalogue, new DroitsFactices("catalogue.creer", "prix.modifier"), journal)
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre, dci) with { NumEnregistrement = num }, 3);
        Assert.Equal((attendue, true), (catalogue.Crees.Single().Classe, catalogue.AVerifier.Single()));
        Assert.Contains($"n° d’enregistrement {num}", cree.Information);
        Assert.Contains("nomenclature nationale", cree.Information);

        // Le titulaire décide lui-même
        var titulaire = new CatalogueEcritureEnMemoire();
        var r = await new CreerProduit(titulaire, new DroitsFactices("catalogue.creer", "prix.modifier") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre, dci) with { NumEnregistrement = num }, 1);
        Assert.Equal((ClasseReglementaire.Libre, null), (titulaire.Crees.Single().Classe, r.Information));
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("psychotrope", null, "psychotrope")]
    [InlineData(null, "psychotrope", "psychotrope")]
    [InlineData("psychotrope", "stupefiant", "stupefiant")]
    [InlineData("stupefiant", "psychotrope", "stupefiant")]
    [InlineData(null, "liste_i", null)]
    public void Classe_sous_registre_la_plus_stricte_des_deux_sources(string? parDci, string? parNomenclature, string? attendue) =>
        Assert.Equal(attendue, RegleProduit.ClasseSurveilleeRetenue(parDci, parNomenclature));

    [Fact]
    public async Task Titulaire_ou_classe_deja_sous_registre_ou_dci_non_surveillee_non_concernes()
    {
        var titulaire = new CatalogueEcritureEnMemoire();
        var r1 = await new CreerProduit(titulaire, new DroitsFactices("catalogue.creer", "prix.modifier") { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Libre), 1);
        Assert.Equal((ClasseReglementaire.Libre, false, null), (titulaire.Crees.Single().Classe, titulaire.AVerifier.Single(), r1.Information));

        var dejaSousRegistre = new CatalogueEcritureEnMemoire();
        var r2 = await new CreerProduit(dejaSousRegistre, new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion())
            .ExecuterAsync(Psychotrope(ClasseReglementaire.Psychotrope), 3);
        Assert.Equal((ClasseReglementaire.Psychotrope, false, null), (dejaSousRegistre.Crees.Single().Classe, dejaSousRegistre.AVerifier.Single(), r2.Information));

        var nonSurveillee = new CatalogueEcritureEnMemoire();
        await new CreerProduit(nonSurveillee, new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion()).ExecuterAsync(Fiche(), 3);
        Assert.Equal((ClasseReglementaire.ListeI, false), (nonSurveillee.Crees.Single().Classe, nonSurveillee.AVerifier.Single()));
    }

    [Fact]
    public async Task Sans_le_droit_refus() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CreerProduit(new CatalogueEcritureEnMemoire(), new DroitsFactices("stock.consulter"), new JournalEspion()).ExecuterAsync(Fiche(), 3));

    [Fact]
    public async Task Doublon_et_code_barres_deja_pris_refuses()
    {
        var cas = new CreerProduit(new CatalogueEcritureEnMemoire(), new DroitsFactices("catalogue.creer", "prix.modifier"), new JournalEspion());
        Assert.Contains("existe déjà", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Fiche("PARACÉTAMOL 500 mg cp", "500 mg"), 2))).Message);
        Assert.Contains("déjà utilisé", (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(Fiche(code: "6130000000107"), 2))).Message);
    }
}
