using Pharma.Application.Achats;
using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Achats;
using Fournisseur = Pharma.Application.Achats.Fournisseur;

namespace Pharma.Application.Tests;

/// <summary>Décisions du titulaire du 2026-11-15 : prix d'achat (bon de commande, réception), patient du parcours, tarif de référence.</summary>
public class DecisionsTitulaire20261115Tests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 8);

    private sealed class DocumentsEspion : IDocumentsAchats
    {
        public bool? AvecPrix { get; private set; }
        public byte[] BonCommande(CommandeDetail commande, Fournisseur fournisseur, string? pharmacie, bool avecPrix) { AvecPrix = avecPrix; return [1]; }
        public bool? RetourAvecPrix { get; private set; }
        public byte[] BonRetour(RetourResume retour, string? pharmacie, bool avecPrix) { RetourAvecPrix = avecPrix; return [2]; }
        public byte[] PlancheEtiquettes(IReadOnlyList<EtiquetteBoite> etiquettes) => [];
        public string EtiquettesZpl(IReadOnlyList<EtiquetteBoite> etiquettes) => "";
    }

    private static AchatsEnMemoire AvecBrouillon() => new()
    {
        Commande = new CommandeDetail(new CommandeResume(3, "C-000003", 2, "Grossiste 2", DateTime.UtcNow, "brouillon", 1, 500m),
            [new LigneCommandeAchat(1, 4, "Metformine", 5, 0, 100m)])
    };

    // ---- Q1 a : bon de commande ----

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Bon_de_commande_imprime_les_prix_seulement_pour_qui_voit_les_prix_d_achat(bool marges, bool titulaire, bool attendu)
    {
        var droits = marges ? new DroitsFactices("achats.commander", "rapports.marges") { Titulaire = titulaire }
            : new DroitsFactices("achats.commander") { Titulaire = titulaire };
        var docs = new DocumentsEspion();
        await new ImprimerCommande(AvecBrouillon(), docs, new PharmacieFactice(), droits).ExecuterAsync(3, 5);
        Assert.Equal(attendu, docs.AvecPrix);

        var docsEmission = new DocumentsEspion();
        await new EmettreCommande(AvecBrouillon(), droits, docsEmission, new PharmacieFactice(), new JournalEspion()).ExecuterAsync(3, 5);
        Assert.Equal(attendu, docsEmission.AvecPrix);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public async Task Bon_de_retour_imprime_l_avoir_attendu_seulement_pour_qui_voit_les_prix_d_achat(bool marges, bool titulaire, bool attendu)
    {
        var droits = marges ? new DroitsFactices("achats.retour_fournisseur", "rapports.marges") { Titulaire = titulaire }
            : new DroitsFactices("achats.retour_fournisseur") { Titulaire = titulaire };
        var achats = new AchatsEnMemoire
        {
            RetourLu = new RetourResume(4, "R-000004", 2, "Grossiste 2", "defaut", "envoye", DateTime.UtcNow, DateTime.UtcNow, null, 850m, null, [])
        };
        var docs = new DocumentsEspion();
        var d = await new SuivreRetour(achats, droits, docs, new PharmacieFactice(), new JournalEspion()).ImprimerAsync(4, 5);
        Assert.NotNull(d);
        Assert.Equal(attendu, docs.RetourAvecPrix);
    }

    // ---- Q1 b : prix de la facture d'achat à la réception ----

    private static SaisieReception Saisie(decimal prix, decimal remise = 0, decimal timbre = 0) =>
        new(1, null, "BL-Q1", "F-1", [new LigneReceptionSaisie(1, "Produit 1", "L1", Aujourdhui.AddYears(1), 4, prix, RemisePct: remise)], timbre);

    [Theory]
    [InlineData(10, 0, 0)]
    [InlineData(0, 5, 0)]
    [InlineData(0, 0, 20)]
    public async Task Reception_avec_prix_refusee_sans_le_droit_de_modifier_un_prix_meme_avec_les_marges(decimal prix, decimal remise, decimal timbre)
    {
        foreach (var droits in new[] { new DroitsFactices("achats.receptionner"), new DroitsFactices("achats.receptionner", "rapports.marges"),
                     new DroitsFactices("achats.receptionner", "rapports.marges") { Titulaire = true } })
        {
            var achats = new AchatsEnMemoire();
            var e = await Assert.ThrowsAsync<DroitRefuseException>(() =>
                new Receptionner(achats, droits, new HorlogeFixe(Aujourdhui), new JournalEspion()).ExecuterAsync(Saisie(prix, remise, timbre), 5));
            Assert.Equal("prix.modifier", e.Permission);   // un seul droit pour toute saisie de prix ; autorisation par code comptoir possible
            Assert.Null(achats.ReceptionRecue);
        }
    }

    [Fact]
    public async Task Reception_avec_prix_acceptee_avec_le_droit_de_modifier_un_prix()
    {
        var achats = new AchatsEnMemoire();
        await new Receptionner(achats, new DroitsFactices("achats.receptionner", "prix.modifier"), new HorlogeFixe(Aujourdhui), new JournalEspion())
            .ExecuterAsync(Saisie(10, 5, 20), 5);
        Assert.NotNull(achats.ReceptionRecue);
    }

    [Fact]
    public async Task Reception_tout_a_zero_refusee_sans_le_droit_revue_I1()
    {
        // Revue 2026-11-15 (I1) : une facture « tout à 0 » ne contourne plus le droit (stock valorisé à 0) ; l'employé compte la livraison
        var achats = new AchatsEnMemoire();
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() => new Receptionner(achats, new DroitsFactices("achats.receptionner"),
            new HorlogeFixe(Aujourdhui), new JournalEspion()).ExecuterAsync(Saisie(0), 5));
        Assert.Equal("prix.modifier", e.Permission);
        Assert.Null(achats.ReceptionRecue);
    }

    [Fact]
    public async Task Facture_de_comptage_tout_a_zero_refusee_sans_le_droit_revue_I1()
    {
        var (comptages, achats) = (new ComptagesEnMemoire(), new AchatsEnMemoire());
        var id = await new CompterLivraison(comptages, achats, new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion())
            .ExecuterAsync(1, "BL-Q1", [new LigneComptee(1, "Produit 1", "L1", Aujourdhui.AddYears(1), 4)], null, 7);
        var receptionner = new Receptionner(achats, new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion());
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() => new FacturerComptage(comptages, receptionner,
            new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion()).ExecuterAsync(id, Saisie(0), 8));
        Assert.Equal("prix.modifier", e.Permission);
        Assert.Equal(StatutsComptage.Compte, comptages.Comptages[id].Statut);
    }

    [Fact]
    public async Task Facture_d_un_comptage_avec_prix_refusee_sans_le_droit_le_comptage_reste_a_facturer()
    {
        var (comptages, achats) = (new ComptagesEnMemoire(), new AchatsEnMemoire());
        var id = await new CompterLivraison(comptages, achats, new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion())
            .ExecuterAsync(1, "BL-Q1", [new LigneComptee(1, "Produit 1", "L1", Aujourdhui.AddYears(1), 4)], null, 7);
        var receptionner = new Receptionner(achats, new DroitsFactices("achats.receptionner"), new HorlogeFixe(Aujourdhui), new JournalEspion());
        var e = await Assert.ThrowsAsync<DroitRefuseException>(() => new FacturerComptage(comptages, receptionner,
            new DroitsFactices("achats.receptionner", "rapports.marges"), new HorlogeFixe(Aujourdhui), new JournalEspion()).ExecuterAsync(id, Saisie(10), 8));
        Assert.Equal("prix.modifier", e.Permission);
        Assert.Null(achats.ReceptionRecue);
        Assert.Equal(StatutsComptage.Compte, comptages.Comptages[id].Statut);

        // Saisie par un ayant droit (autre personne que le compteur) : la livraison entre en stock
        var r = await new FacturerComptage(comptages, receptionner, new DroitsFactices("achats.receptionner", "prix.modifier"),
            new HorlogeFixe(Aujourdhui), new JournalEspion()).ExecuterAsync(id, Saisie(10), 8);
        Assert.NotNull(r.Reception);
    }

    // ---- Q2 : patient du parcours d'une boîte ----

    private sealed class ParcoursFixe(bool sousRegistre) : ITracabiliteLecture
    {
        public Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default) =>
            Task.FromResult<ParcoursBoite?>(new ParcoursBoite(1, codeOuSerie, "Produit", "L1", Aujourdhui, "Grossiste", "BL", DateTime.UtcNow,
                "vendue", false, "T-1", DateTime.UtcNow, null, "Patient Dupont", [], 4, sousRegistre));
    }

    [Theory]
    [InlineData(true, "psychotropes.registre", false, true)]
    [InlineData(true, "stock.declarer_perime", false, false)]
    [InlineData(true, "stock.consulter", true, true)]
    [InlineData(false, "stock.declarer_perime", false, true)]
    [InlineData(false, "psychotropes.registre", false, false)]
    [InlineData(false, "stock.consulter", false, false)]
    [InlineData(false, "stock.consulter", true, true)]
    public async Task Nom_du_patient_selon_le_produit_et_le_droit(bool sousRegistre, string droit, bool titulaire, bool visible)
    {
        var p = await new ConsulterParcoursBoite(new ParcoursFixe(sousRegistre), new DroitsFactices("stock.consulter", droit) { Titulaire = titulaire })
            .ExecuterAsync("2000000001272", 5);
        Assert.NotNull(p);                                       // le parcours reste visible
        Assert.Equal(visible ? "Patient Dupont" : null, p!.Patient);
        Assert.Equal("T-1", p.Ticket);
    }

    // ---- Q3 : tarif de référence et remboursable ----

    [Fact]
    public async Task Tarif_de_reference_et_remboursable_exigent_le_droit_de_modifier_un_prix()
    {
        var p = new ProduitsModifEnMemoire();
        var e1 = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { TarifReference = 95m }, 1));
        Assert.Equal("prix.modifier", e1.Permission);
        var e2 = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { Remboursable = false }, 1));
        Assert.Equal("prix.modifier", e2.Permission);
        // Titulaire compris : le droit est vérifié comme pour le prix public
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ModifierProduit(p, new DroitsFactices("catalogue.creer") { Titulaire = true }, new JournalEspion()).ExecuterAsync(p.Fiche with { TarifReference = 95m }, 1));
        Assert.Null(p.Modifiee);

        var journal = new JournalEspion();
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer", "prix.modifier"), journal)
            .ExecuterAsync(p.Fiche with { TarifReference = 95m, Remboursable = false }, 1);
        Assert.Equal((95m, false), (p.Modifiee!.TarifReference, p.Modifiee.Remboursable));
        Assert.Equal(["produit_modifie"], journal.Actions);
    }

    [Fact]
    public async Task Fiche_modifiee_sans_toucher_au_tarif_ni_au_remboursement_n_exige_pas_le_droit_des_prix()
    {
        var p = new ProduitsModifEnMemoire();
        await new ModifierProduit(p, new DroitsFactices("catalogue.creer"), new JournalEspion()).ExecuterAsync(p.Fiche with { Dosage = "20 mg" }, 1);
        Assert.Equal("20 mg", p.Modifiee!.Dosage);
    }
}
