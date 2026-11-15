using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Table motif_reglable en mémoire, avec les valeurs de départ du script 094 utiles aux tests.</summary>
internal sealed class MotifsEnMemoire : IMotifsReglables
{
    public List<MotifReglable> Motifs { get; } =
    [
        new(1, "tiroir", "monnaie", "Faire de la monnaie", true, false, 10),
        new(2, "tiroir", "autre", "Autre (préciser)", true, true, 999),
        new(3, "tiroir", "ancien", "Ancien motif", false, false, 20),
        new(4, "attente", "produit_manquant", "Produit manquant à commander", true, false, 10),
        new(5, "attente", "patient_revient", "Patient revient payer", true, false, 20),
        new(6, "attente", "autre", "Autre (préciser)", true, true, 999),
        new(7, "charge", "loyer", "Loyer et charges", true, false, 10),
        new(8, "sortie_stock", "perime", "Périmé", true, true, 10)
    ];

    public Task<IReadOnlyList<MotifReglable>> ListerAsync(string? liste, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MotifReglable>>(Motifs.Where(m => liste is null || m.Liste == liste).ToList());

    public Task<long> CreerAsync(string liste, string code, string libelle, CancellationToken ct = default)
    {
        var id = Motifs.Max(m => m.Id) + 1;
        Motifs.Add(new MotifReglable(id, liste, code, libelle, true, false, 100));
        return Task.FromResult(id);
    }

    public Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default)
    {
        var i = Motifs.FindIndex(m => m.Id == id);
        if (i < 0) return Task.FromResult(false);
        Motifs[i] = Motifs[i] with { Libelle = libelle, Actif = actif };
        return Task.FromResult(true);
    }
}

public class MotifsReglablesTests
{
    private static (SignalerEvenementCaisse Cas, SurveillanceEnMemoire Depot, JournalEspion Journal) Tiroir()
    {
        var (depot, journal) = (new SurveillanceEnMemoire(), new JournalEspion());
        var cas = new SignalerEvenementCaisse(depot, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre", "caisse.ouvrir_tiroir"),
            new Surveiller(depot), journal, new MotifsActifs(new MotifsEnMemoire()));
        return (cas, depot, journal);
    }

    [Theory]
    [InlineData(null, null)]          // motif obligatoire
    [InlineData("  ", null)]
    [InlineData("inconnu", null)]
    [InlineData("ancien", null)]      // désactivé : plus proposé ni accepté
    [InlineData("autre", null)]       // « Autre » sans texte
    [InlineData("autre", "   ")]
    public async Task Tiroir_motif_obligatoire_actif_et_autre_avec_texte(string? code, string? texte)
    {
        var (cas, depot, journal) = Tiroir();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("tiroir_ouvert", null, null, null, null, 5, "Caisse 1", default, code, texte));
        Assert.Empty(depot.Evenements);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Tiroir_motif_inscrit_avec_l_evenement_et_au_journal()
    {
        var (cas, depot, journal) = Tiroir();
        await cas.ExecuterAsync("tiroir_ouvert", null, null, null, null, 5, "Caisse 1", default, "autre", "  Rendu au livreur ");
        Assert.Equal("Autre : Rendu au livreur", depot.Evenements.Single().Detail);
        Assert.Equal(("Autre : Rendu au livreur", "autre", "Caisse 1"),
            ((string?)journal.Details.Single()["motif"], (string?)journal.Details.Single()["motif_code"], (string?)journal.Details.Single()["poste"]));

        await cas.ExecuterAsync("tiroir_ouvert", null, null, null, null, 5, "Caisse 1", default, "monnaie", "ignoré hors Autre");
        Assert.Equal("Faire de la monnaie", depot.Evenements[1].Detail);
    }

    [Fact]
    public async Task Desactivation_sans_suppression_jamais_d_un_motif_systeme()
    {
        var depot = new MotifsEnMemoire();
        var (enregistrer, journal) = (new EnregistrerMotifReglable(depot, new DroitsFactices("admin.parametres"), new JournalEspion()), 0);
        var nombre = depot.Motifs.Count;

        await enregistrer.ExecuterAsync(1, "tiroir", "Faire de la monnaie", false, 9);
        Assert.Equal(nombre, depot.Motifs.Count);                         // toujours là, désactivé
        Assert.False(depot.Motifs.Single(m => m.Id == 1).Actif);
        Assert.DoesNotContain(await new MotifsActifs(depot).ExecuterAsync("tiroir"), m => m.Code == "monnaie");

        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(2, "tiroir", "Autre (préciser)", false, 9));   // « Autre »
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(8, "sortie_stock", "Périmé", false, 9));      // périmé
        Assert.True(depot.Motifs.Single(m => m.Id == 2).Actif);
        _ = journal;
    }

    [Fact]
    public async Task Ajout_et_libelle_regles_par_l_administration_seulement()
    {
        var depot = new MotifsEnMemoire();
        var sansDroit = new EnregistrerMotifReglable(depot, new DroitsFactices("caisse.vendre"), new JournalEspion());
        await Assert.ThrowsAsync<DroitRefuseException>(() => sansDroit.ExecuterAsync(null, "tiroir", "Dépôt banque", true, 9));

        var journal = new JournalEspion();
        var enregistrer = new EnregistrerMotifReglable(depot, new DroitsFactices("admin.parametres"), journal);
        var id = await enregistrer.ExecuterAsync(null, "charge", "Téléphone et Internet", true, 9);
        Assert.Equal("telephone_et_internet", depot.Motifs.Single(m => m.Id == id).Code);
        Assert.Equal(["motif_reglable"], journal.Actions);
        // Doublon de libellé, liste inconnue, ajout dans une liste à codes fixes : refusés
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "charge", "téléphone et internet", true, 9));
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "inconnue", "X", true, 9));
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "sortie_stock", "Vol", true, 9));
        await Assert.ThrowsAsync<RegleMetierException>(() => enregistrer.ExecuterAsync(null, "tiroir", "  ", true, 9));
    }

    [Fact]
    public void Code_tire_du_libelle_unique()
    {
        Assert.Equal("depot_d_especes", RegleMotifs.CodeDepuisLibelle("Dépôt d’espèces", []));
        Assert.Equal("depot_especes_2", RegleMotifs.CodeDepuisLibelle("Dépôt espèces", ["depot_especes"]));
    }

    [Fact]
    public async Task Mise_en_attente_avec_motif_produit_manquant_et_produits_dus()
    {
        var stock = new AttenteEnMemoire();
        var gerer = new GererVentesEnAttente(stock, motifs: new MotifsActifs(new MotifsEnMemoire()));

        // « Produit manquant » sans produit dû, « Autre » sans texte, motif inconnu : refusés
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}", default, "produit_manquant"));
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}", default, "autre"));
        await Assert.ThrowsAsync<RegleMetierException>(() => gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}", default, "inconnu"));
        Assert.Empty(stock.Paniers);

        var id = await gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 2, 0, "{}", default, "produit_manquant", null,
            [new ProduitDu(18, " DOLIPRANE 1G ", 2), new ProduitDu(19, "X", 0)]);
        var m = stock.Motifs[id]!;
        Assert.Equal(("produit_manquant", 1, "DOLIPRANE 1G"), (m.Code, m.ProduitsDus!.Count, m.ProduitsDus[0].Nom));

        var autre = await gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}", default, "autre", "Carte Chifa oubliée");
        Assert.Equal(("autre", "Carte Chifa oubliée", (IReadOnlyList<ProduitDu>?)null), (stock.Motifs[autre]!.Code, stock.Motifs[autre]!.Texte, stock.Motifs[autre]!.ProduitsDus));

        // Mise en attente automatique (départ de la caisse, changement d'utilisateur) : sans motif
        var auto = await gerer.MettreEnAttenteAsync("Caisse 1", 5, null, 1, 0, "{}");
        Assert.Null(stock.Motifs[auto]);
    }

    [Fact]
    public void Nombre_de_vignettes_somme_des_boites_du_volet()
    {
        Assert.Equal(6, VignettesChifa.Nombre([2, 1, 3]));
        Assert.Equal(3, VignettesChifa.Nombre([3, 0, -1]));
        Assert.Equal(0, VignettesChifa.Nombre([]));
        var volet = new VoletChifaDto("F1", new DateOnly(2026, 11, 15), "A", "", "", null, 100m,
            [new("C1", 2, 10m, null, null, null), new("C2", 1, 10m, null, null, null)], null, null, null, "brut");
        Assert.Equal(3, VignettesChifa.Nombre(volet));
    }
}
