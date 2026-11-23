using Pharma.Application.Caisse;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Indications thérapeutiques et motif réglable du retour client (2026-11-15, script 104).</summary>
public class IndicationsTests
{
    internal sealed class IndicationsEnMemoire : IIndications
    {
        public List<Indication> Toutes { get; } = [new(1, "Toux", true), new(2, "Fièvre", true), new(3, "Ancienne", false)];
        public Dictionary<long, List<long>> ParProduit { get; } = new() { [10] = [] };

        public Task<Page<Indication>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default) =>
            Task.FromResult(new Page<Indication>(Toutes.Where(i => actif is null || i.Actif == actif).ToList(), Toutes.Count, 1, 20));
        public Task<IReadOnlyList<Indication>> ParIdsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Indication>>(Toutes.Where(i => ids.Contains(i.Id)).ToList());
        public Task<long> EnregistrerAsync(long? id, string libelle, bool actif, CancellationToken ct = default)
        {
            if (Toutes.Any(i => i.Id != id && string.Equals(i.Libelle, libelle, StringComparison.OrdinalIgnoreCase)))
                throw new RegleMetierException($"L’indication « {libelle} » existe déjà.", "libelle");
            var n = id ?? Toutes.Max(i => i.Id) + 1;
            Toutes.RemoveAll(i => i.Id == n);
            Toutes.Add(new Indication(n, libelle, actif));
            return Task.FromResult(n);
        }
        public Task<IReadOnlyList<Indication>> DuProduitAsync(long produitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Indication>>(Toutes.Where(i => ParProduit[produitId].Contains(i.Id)).ToList());
        public Task RemplacerDuProduitAsync(long produitId, IReadOnlyList<long> ids, CancellationToken ct = default)
        {
            ParProduit[produitId] = ids.ToList();
            return Task.CompletedTask;
        }
    }

    private static (Indications Cas, IndicationsEnMemoire Base, JournalEspion Journal) Monter()
    {
        var b = new IndicationsEnMemoire();
        var journal = new JournalEspion();
        var droits = new DroitsParEmploye(new() { [1] = ["catalogue.creer"], [2] = ["caisse.vendre"] });
        return (new Indications(b, droits, journal), b, journal);
    }

    [Fact]
    public async Task Association_plusieurs_indications_sans_doublon_et_journalisee()
    {
        var (cas, b, journal) = Monter();
        await cas.AssocierAsync(10, [1, 2, 1], 1);
        Assert.Equal([1L, 2L], b.ParProduit[10]);
        Assert.Equal("produit_indications", journal.Actions.Single());

        // Retrait : une seule reste ; aucun changement : rien n'est journalisé
        await cas.AssocierAsync(10, [2], 1);
        Assert.Equal([2L], b.ParProduit[10]);
        await cas.AssocierAsync(10, [2], 1);
        Assert.Equal(2, journal.Actions.Count);
    }

    [Fact]
    public async Task Association_refuse_indication_desactivee_ou_inconnue_et_sans_droit()
    {
        var (cas, b, _) = Monter();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AssocierAsync(10, [3], 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AssocierAsync(10, [99], 1));
        await Assert.ThrowsAnyAsync<Exception>(() => cas.AssocierAsync(10, [1], 2));
        Assert.Empty(b.ParProduit[10]);

        // Déjà associée puis désactivée : elle peut rester
        b.ParProduit[10] = [3];
        await cas.AssocierAsync(10, [3, 1], 1);
        Assert.Equal([3L, 1L], b.ParProduit[10]);
    }

    [Fact]
    public async Task Libelle_obligatoire_et_unique()
    {
        var (cas, _, journal) = Monter();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.EnregistrerAsync(null, "  ", true, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.EnregistrerAsync(null, "toux", true, 1));
        var i = await cas.EnregistrerAsync(null, "  Mal   de gorge ", true, 1);
        Assert.Equal(("Mal de gorge", true), (i.Libelle, i.Actif));
        Assert.Equal("indication_creee", journal.Actions.Single());
    }

    // ---- Recherche au comptoir par indication ----

    private static ProduitResume Resume(long id, int stock) => new(id, "P" + id, null, null, null, 300, stock, ClasseReglementaire.Libre);

    private sealed class CatalogueIndications(ProduitResume[] parNom, ProduitResume[] parIndication) : ICatalogueLecture
    {
        public (string? Texte, long? Id)? Demande { get; private set; }
        public Task<IReadOnlyList<ProduitResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default, bool inclureInactifs = false, bool auComptoir = false) =>
            Task.FromResult<IReadOnlyList<ProduitResume>>(parNom);
        public Task<IReadOnlyList<ProduitResume>> ParIndicationAsync(string? texte, long? indicationId, int limite, CancellationToken ct = default)
        {
            Demande = (texte, indicationId);
            return Task.FromResult<IReadOnlyList<ProduitResume>>(parIndication);
        }
    }

    private sealed class SansEquivalent : IEquivalentsComptoir
    {
        public Task<IReadOnlyList<ProduitPrixServi>> ProduitsAsync(IReadOnlyCollection<long> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProduitPrixServi>>([]);
        public Task<IReadOnlyList<ProduitPrixServi>> EnStockAsync(IReadOnlyCollection<long> dciIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ProduitPrixServi>>([]);
    }

    [Fact]
    public async Task Recherche_toux_ajoute_les_produits_de_l_indication_disponibles_d_abord()
    {
        // 1 trouvé par son nom (rupture) ; 2 (disponible), 3 (rupture) et 1 (déjà trouvé) par l'indication « Toux »
        var catalogue = new CatalogueIndications([Resume(1, 0)], [Resume(2, 5), Resume(1, 0), Resume(3, 0)]);
        var r = await new RechercherAuComptoir(catalogue, new SansEquivalent()).ExecuterAsync("toux", null);
        Assert.Equal([2L, 1L, 3L], r.Select(c => c.Produit.Id));
        Assert.Equal(("toux", (long?)null), catalogue.Demande);
    }

    [Fact]
    public async Task Recherche_par_identifiant_d_indication_et_texte_trop_court()
    {
        var catalogue = new CatalogueIndications([Resume(1, 2)], [Resume(4, 0), Resume(5, 3)]);
        var cas = new RechercherAuComptoir(catalogue, new SansEquivalent());
        Assert.Equal([5L, 4L], (await cas.ExecuterAsync(null, null, indicationId: 7)).Select(c => c.Produit.Id));
        Assert.Equal((null, (long?)7), catalogue.Demande);

        // Deux lettres : pas de recherche par indication
        var court = new CatalogueIndications([Resume(1, 2)], [Resume(4, 0)]);
        Assert.Equal([1L], (await new RechercherAuComptoir(court, new SansEquivalent()).ExecuterAsync("to", null)).Select(c => c.Produit.Id));
        Assert.Null(court.Demande);
    }

    // ---- Motif réglable du retour client ----

    private static VenteRetournable Vente() => new(
        new VenteAnnulable(30, "T-000030", DateTime.UtcNow, "Vendeur", null, 300, 300, 0, "especes", "validee", false, false, []),
        [new LigneRetournable(5, 15, "Gélule fictive", "G1", 3, 0, 300, [])]);

    private static (EnregistrerRetourClient Cas, RetoursEnMemoire Stock, JournalEspion Journal) MonterRetour()
    {
        var motifs = new MotifsEnMemoire();
        motifs.Motifs.Add(new MotifReglable(50, RegleMotifs.RetourClient, "erreur_delivrance", "Erreur de délivrance", true, false, 10));
        motifs.Motifs.Add(new MotifReglable(51, RegleMotifs.RetourClient, "autre", "Autre (préciser)", true, true, 999));
        var stock = new RetoursEnMemoire(Vente());
        var journal = new JournalEspion();
        var droits = new DroitsParEmploye(new() { [8] = ["caisse.vendre", "caisse.retour_especes"] });
        return (new EnregistrerRetourClient(stock, new SessionsEnMemoire(), droits,
            new VerificationCodeComptoir(new CodesEnMemoire(), new VerificateurFactice(), new HorlogeFixe(new DateOnly(2026, 11, 15)), journal),
            motifs: new MotifsActifs(motifs)), stock, journal);
    }

    [Fact]
    public async Task Motif_de_retour_obligatoire_et_choisi_dans_la_liste()
    {
        var (cas, stock, journal) = MonterRetour();
        LigneRetourDemandee[] l = [new(5, 1, "stock", null)];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(30, "Traitement arrêté", l, 8, "Caisse 1", null, null));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(30, null, l, 8, "Caisse 1", null, null, motifCode: "inconnu"));
        Assert.Null(stock.Ecrit);

        await cas.ExecuterAsync(30, null, l, 8, "Caisse 1", null, null, motifCode: "erreur_delivrance");
        Assert.Equal(("Erreur de délivrance", "erreur_delivrance", (string?)null), (stock.Ecrit!.Motif, stock.Ecrit.MotifCode, stock.Ecrit.MotifTexte));
        Assert.Equal("erreur_delivrance", journal.Details.Last()["motif_code"]);
    }

    [Fact]
    public async Task Motif_autre_exige_un_texte()
    {
        var (cas, stock, _) = MonterRetour();
        LigneRetourDemandee[] l = [new(5, 1, "stock", null)];
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(30, "  ", l, 8, "Caisse 1", null, null, motifCode: "autre"));
        await cas.ExecuterAsync(30, "Mauvais dosage", l, 8, "Caisse 1", null, null, motifCode: "autre");
        Assert.Equal(("Autre : Mauvais dosage", "autre", "Mauvais dosage"), (stock.Ecrit!.Motif, stock.Ecrit.MotifCode, stock.Ecrit.MotifTexte));
    }
}
