using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Prestations sans stock à la caisse (2026-11-15, script 105).</summary>
public class PrestationsCaisseTests
{
    private static Encaisser Cas(VentesEspion ventes) =>
        new(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire());

    private static CommandeVente Commande(params LigneCommande[] lignes) =>
        new(lignes, false, null, null, null, null, 0, ModePaiement.Especes, 100_000m);

    private static LigneCommande Prestation(long id, int qte = 1) => new(null, null, qte, TypePrestationId: id);

    [Fact]
    public async Task Vente_mixte_produit_et_prestation_enregistree_sans_stock_ni_registre_pour_la_prestation()
    {
        var ventes = new VentesEspion();
        var r = await Cas(ventes).ExecuterAsync(Commande(new LigneCommande(2, 20, 1), Prestation(1, 2)), 5, "Caisse 1");

        Assert.Equal(750m, r.MontantTotal);   // 350 + 2 x 200
        var lignes = ventes.Recu!.Vente.Lignes;
        Assert.Equal((2L, 20L), (lignes[0].ProduitId!.Value, lignes[0].LotId!.Value));
        var p = lignes[1];
        Assert.Equal((null, null, 1L, "Prise de tension", 19m, 0m), (p.ProduitId, p.LotId, p.TypePrestationId, p.Libelle, p.TauxTva, p.Shp));
        Assert.Empty(ventes.Recu.ProduitsSousRegistre);
        Assert.Null(ventes.Recu.BoitesParLot);
    }

    [Fact]
    public async Task Panier_de_prestations_seules_calcule_et_encaisse()
    {
        var ventes = new VentesEspion();
        var cas = Cas(ventes);
        var apercu = await cas.CalculerAsync(Commande(Prestation(1)), 5);
        Assert.Equal((200m, 200m, 0m), (apercu.MontantTotal, apercu.PartPatient, apercu.TotalMedicaments));
        Assert.Equal("aucune", apercu.Situations![0].Situation);
        await cas.ExecuterAsync(Commande(Prestation(1)), 5, "Caisse 1");
        Assert.Single(ventes.Recu!.Vente.Lignes);
    }

    [Theory]
    [InlineData(2L, null, null)]   // produit sans lot
    [InlineData(2L, 20L, 1L)]      // produit et prestation sur la même ligne
    [InlineData(null, 20L, 1L)]    // lot et prestation
    [InlineData(null, null, null)] // rien
    public async Task Ligne_ni_produit_et_lot_ni_prestation_refusee(long? produit, long? lot, long? prestation)
    {
        var ventes = new VentesEspion();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(ventes).ExecuterAsync(Commande(new LigneCommande(produit, lot, 1, TypePrestationId: prestation)), 5, "Caisse 1"));
        Assert.Equal("lignes", ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Prestation_avec_des_boites_refusee() =>
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new VentesEspion()).ExecuterAsync(Commande(new LigneCommande(null, null, 1, [5], TypePrestationId: 1)), 5, "Caisse 1"));

    [Theory]
    [InlineData(2)]    // prix non fixé
    [InlineData(3)]    // désactivée
    [InlineData(99)]   // inconnue
    public async Task Prestation_invendable_refusee(long id)
    {
        var ventes = new VentesEspion();
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(ventes).ExecuterAsync(Commande(Prestation(id)), 5, "Caisse 1"));
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Meme_panier_avec_prestation_renvoye_avec_sa_cle_rend_la_vente_deja_enregistree()
    {
        var ventes = new VentesEspion();
        var cas = Cas(ventes);
        var commande = Commande(new LigneCommande(2, 20, 1), Prestation(1)) with { CleEncaissement = Guid.NewGuid() };
        await cas.ExecuterAsync(commande, 5, "Caisse 1");
        await cas.ExecuterAsync(commande, 5, "Caisse 1");
        Assert.Equal(1, ventes.Enregistrements);
        // Même clé, prestation en plus : contenu différent, refusé
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.ExecuterAsync(commande with { Lignes = [new LigneCommande(2, 20, 1), Prestation(1, 2)] }, 5, "Caisse 1"));
    }

    [Fact]
    public void Ticket_la_prestation_porte_son_taux_de_tva()
    {
        Assert.Equal("Prise de tension (prestation, TVA 19 %)", new LigneTicket("Prise de tension", 1, 200m, 200m, 0m, TauxTva: 19m).Designation);
        Assert.Equal("Amoxicilline", new LigneTicket("Amoxicilline", 1, 350m, 350m, 0m).Designation);
        Assert.Equal("Injection (prestation, TVA 9,5 %)", LibelleTicket.Ligne("Injection", 9.5m));
    }
}

internal sealed class TypesPrestationEnMemoire : ITypesPrestation
{
    public Dictionary<long, TypePrestation> Types { get; } = [];
    public Task<Page<TypePrestation>> ListerAsync(bool? actif, string? recherche, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(new Page<TypePrestation>(Types.Values.Where(t => actif is null || t.Actif == actif).ToList(), Types.Count, page.Page, page.Taille));
    public Task<TypePrestation?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Types.GetValueOrDefault(id));
    public Task<long> EnregistrerAsync(long? id, string code, string libelle, decimal prix, decimal tauxTva, bool actif,
        bool remboursable, decimal? tarifReference, CancellationToken ct = default)
    {
        var n = id ?? Types.Count + 1;
        Types[n] = new TypePrestation(n, code, libelle, prix, tauxTva, actif, remboursable, tarifReference);
        return Task.FromResult(n);
    }
}

public class TypesPrestationTests
{
    [Fact]
    public async Task Creation_exige_le_droit_de_creer_au_catalogue()
    {
        var types = new TypesPrestationEnMemoire();
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new TypesPrestation(types, new DroitsFactices("caisse.vendre"), new JournalEspion()).EnregistrerAsync(null, "TENSION", "Prise de tension", 0, 0, true, 3));
        Assert.Equal("catalogue.creer", ex.Permission);
        Assert.Empty(types.Types);
    }

    [Fact]
    public async Task Creation_puis_modification_journalisees()
    {
        var types = new TypesPrestationEnMemoire();
        var journal = new JournalEspion();
        var service = new TypesPrestation(types, new DroitsFactices("catalogue.creer"), journal);
        var t = await service.EnregistrerAsync(null, " tension ", "Prise de tension", 0, 0, true, 3);
        Assert.Equal(("TENSION", 0m), (t.Code, t.Prix));
        await service.EnregistrerAsync(t.Id, "TENSION", "Prise de tension", 200m, 19m, true, 3);
        Assert.Equal(["type_prestation_cree", "type_prestation_modifie"], journal.Actions);
        Assert.Equal(200m, types.Types[t.Id].Prix);
    }

    [Fact]
    public async Task Liste_reservee_aux_employes_qui_vendent() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new TypesPrestation(new TypesPrestationEnMemoire(), new DroitsFactices(), new JournalEspion()).ListerAsync(true, null, new DemandePage(), 3));
}
