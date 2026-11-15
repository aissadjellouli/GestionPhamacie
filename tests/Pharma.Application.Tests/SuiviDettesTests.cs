using Pharma.Application.Suivi;

namespace Pharma.Application.Tests;

public class SuiviDettesTests
{
    private static readonly DateOnly Jour = new(2026, 11, 10);

    private static SommeDue Somme(string sens, string categorie, string tiers, int joursAvant, decimal montant, bool rejetee = false, int? echeanceDans = null) =>
        new(sens, categorie, 1, tiers, "Pièce", Jour.AddDays(-joursAvant), echeanceDans is { } e ? Jour.AddDays(e) : null, montant, rejetee);

    [Fact]
    public void Alerte_au_dela_du_delai_pour_tous_les_tiers()
    {
        var s = RegleSuiviDettes.Construire(
        [
            Somme("creance", "patient", "Patient A", 45, 500),          // en retard
            Somme("creance", "patient", "Patient B", 10, 300),          // dans le délai
            Somme("creance", "facture_organisme", "CNAS", 5, 900, rejetee: true),   // rejetée : signalée quel que soit son âge
            Somme("creance", "bordereau", "CASNOS", 70, 2000),
            Somme("dette", "grossiste", "Grossiste A", 31, 4000),
            Somme("dette", "effet", "Grossiste A", 20, 1500, echeanceDans: 3),      // à payer sous 7 jours
            Somme("dette", "effet", "Grossiste B", 20, 1000, echeanceDans: 40)      // plus tard : pas d'alerte
        ], delaiJours: 30, Jour);

        Assert.Equal(["bordereau", "facture_organisme", "patient", "effet", "grossiste"],
            s.Alertes.OrderBy(a => a.Sens).ThenBy(a => a.Categorie).Select(a => a.Categorie));
        Assert.Equal((1, 500m), s.Alertes.Where(a => a.Categorie == "patient").Select(a => (a.Nombre, a.Montant)).Single());
        Assert.Equal(1500m, s.Alertes.Single(a => a.Categorie == "effet").Montant);
        Assert.Contains("rejetée", s.Alertes.Single(a => a.Categorie == "facture_organisme").Message);
    }

    [Fact]
    public void Chifa_ventes_a_deposer_et_bordereaux_impayes_selon_leur_echeance()
    {
        // Échéance calculée par v_suivi_dettes (script 110) : vente + délai de dépôt, dépôt + délai de paiement ; le délai général n'y joue pas
        var s = RegleSuiviDettes.Construire(
        [
            Somme("creance", "chifa_a_deposer", "CNAS", 8, 800, echeanceDans: -1),     // 7 jours de délai dépassés
            Somme("creance", "chifa_a_deposer", "CNAS", 3, 500, echeanceDans: 4),      // dans le délai
            Somme("creance", "bordereau", "CNAS", 50, 9000, echeanceDans: -5),         // déposé il y a 50 jours, 45 de délai
            Somme("creance", "bordereau", "CASNOS", 40, 3000, echeanceDans: 5)         // dans le délai, même au-delà des 30 jours généraux
        ], delaiJours: 30, Jour);

        Assert.Equal((1, 800m), s.Alertes.Where(a => a.Categorie == "chifa_a_deposer").Select(a => (a.Nombre, a.Montant)).Single());
        Assert.Equal((1, 9000m), s.Alertes.Where(a => a.Categorie == "bordereau").Select(a => (a.Nombre, a.Montant)).Single());
        Assert.Contains("à déposer en retard", s.Alertes.Single(a => a.Categorie == "chifa_a_deposer").Message);
    }

    [Fact]
    public void Echeancier_par_tranche_et_totaux_sans_les_effets()
    {
        var s = RegleSuiviDettes.Construire(
        [
            Somme("creance", "patient", "Patient A", 5, 100),
            Somme("creance", "patient", "Patient B", 45, 200),
            Somme("creance", "facture_entreprise", "Entreprise", 90, 300),
            Somme("dette", "grossiste", "Grossiste A", 61, 4000),
            Somme("dette", "effet", "Grossiste A", 10, 1500, echeanceDans: 30)
        ], 30, Jour);

        Assert.Equal((600m, 4000m), (s.TotalCreances, s.TotalDettes));   // l'effet a déjà diminué la dette : hors total
        Assert.Equal([("creance", "0-30", 100m), ("creance", "31-60", 200m), ("creance", "61+", 300m), ("dette", "61+", 4000m)],
            s.Tranches.Select(t => (t.Sens, t.Tranche, t.Montant)));
        Assert.True(s.Lignes[0].EnAlerte);   // les alertes d'abord
    }
}

public class FiltrageSuiviDettesTests
{
    private static readonly DateOnly Jour = new(2026, 11, 10);

    private static SuiviDettes Suivi() => RegleSuiviDettes.Construire(
    [
        new("creance", "patient", 1, "Patient A", "Vente T-1", Jour.AddDays(-45), null, 500, false),   // en retard
        new("creance", "patient", 1, "Patient A", "Vente T-2", Jour.AddDays(-5), null, 100, false),
        new("creance", "patient", 2, "Patient B", "Vente T-3", Jour.AddDays(-10), null, 300, false),
        new("creance", "patient", 3, "Patient B", "Vente T-4", Jour.AddDays(-12), null, 50, false),     // homonyme : autre patient
        new("creance", "facture_entreprise", 9, "Entreprise", "Facture F-1", Jour.AddDays(-20), Jour.AddDays(10), 900, true),
        new("dette", "grossiste", 7, "Grossiste A", "Facture 1", Jour.AddDays(-31), null, 4000, false)
    ], delaiJours: 30, Jour);

    [Fact]
    public void Categorie_tiers_et_total_de_tout_le_filtre()
    {
        var r = FiltrageSuiviDettes.Filtrer(Suivi(), new FiltreSuiviDettes("patient"), new Pharma.Application.Commun.DemandePage(1, 2));
        Assert.Equal(4, r.Page.Total);                       // toutes les lignes de la catégorie, pas seulement la page
        Assert.Equal(2, r.Page.Lignes.Count);
        Assert.Equal(950m, r.Total);                         // total de toutes les pages
        Assert.True(r.Page.Lignes[0].EnAlerte);              // même ordre que le suivi : alertes d'abord
        Assert.Empty(r.Tiers);                               // patients : pas de liste de tous les noms, seulement une recherche
    }

    [Fact]
    public void Recherche_du_tiers_avec_propositions_sans_majuscules_ni_accents()
    {
        var s = Suivi();
        var page = new Pharma.Application.Commun.DemandePage();
        var r = FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Recherche: "PATIËNT b"), page);
        // Deux « Patient B » distingués par leur n° ; le tableau suit la saisie
        Assert.Equal(["Patient B (n° 2)", "Patient B (n° 3)"], r.Tiers.Select(t => t.Nom));
        Assert.Equal(350m, r.Total);
        Assert.Empty(FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Recherche: "b"), page).Tiers);   // moins de 2 lettres
        // Grossistes : peu nombreux, liste complète sans saisie
        Assert.Equal(["Grossiste A"], FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("grossiste"), page).Tiers.Select(t => t.Nom));
    }

    [Fact]
    public void Filtres_tiers_etat_et_periode()
    {
        var s = Suivi();
        var page = new Pharma.Application.Commun.DemandePage();
        Assert.Equal(600m, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Tiers: "1"), page).Total);
        Assert.Equal(500m, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Etat: "alerte"), page).Total);
        Assert.Equal(450m, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Etat: "delai"), page).Total);
        Assert.Equal(900m, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("facture", Etat: "rejetee"), page).Total);
        Assert.Equal(400m, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes("patient", Du: Jour.AddDays(-10), Au: Jour), page).Total);
        Assert.Equal(6, FiltrageSuiviDettes.Filtrer(s, new FiltreSuiviDettes(), page).Page.Total);   // « Tout »
    }

    [Fact]
    public void Valeurs_inconnues_ignorees()
    {
        var f = FiltreSuiviDettes.Lire("inconnue", "  ", "bizarre", null, null);
        Assert.Equal(new FiltreSuiviDettes(), f);
    }
}

public class SuiviFacturesEcheanceTests
{
    private static readonly DateOnly Jour = new(2026, 11, 10);

    [Fact]
    public void Une_facture_est_en_alerte_quand_son_echeance_est_depassee()
    {
        // Établie il y a 50 jours mais échéance dans 10 jours : pas d'alerte ; établie il y a 5 jours, échue hier : alerte
        SommeDue[] sommes =
        [
            new("creance", "facture_entreprise", 1, "Entreprise A", "Facture F-1", Jour.AddDays(-50), Jour.AddDays(10), 1000, false),
            new("creance", "facture_entreprise", 2, "Entreprise B", "Facture F-2", Jour.AddDays(-5), Jour.AddDays(-1), 700, false),
            new("creance", "facture_organisme", 3, "CNAS", "Facture F-3", Jour.AddDays(-2), Jour.AddDays(28), 900, true)   // rejetée : toujours signalée
        ];
        var s = RegleSuiviDettes.Construire(sommes, delaiJours: 30, Jour);

        Assert.Equal(["Facture F-2", "Facture F-3"], s.Lignes.Where(l => l.EnAlerte).Select(l => l.Somme.Libelle).Order());
        Assert.Equal(700m, s.Alertes.Single(a => a.Categorie == "facture_entreprise").Montant);
        Assert.Contains("échue", s.Alertes.Single(a => a.Categorie == "facture_entreprise").Message);
    }
}
