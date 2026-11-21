using Pharma.Desktop.Services;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Barre d'état du poste (maquette validée le 2026-11-21) : une pastille seulement s'il y a une information, lien pour qui a le droit,
/// regroupement des pastilles grises dès qu'une orange ou rouge s'affiche, imprimante pour tous, serveur injoignable.
/// </summary>
public class PastillesBarreTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 21, 10, 42, 0, DateTimeKind.Local);

    private static Func<string, bool> Droits(params string[] d) => d.Contains;

    private static readonly string[] Titulaire = ["role.titulaire", "caisse.vendre", "chifa.bordereaux", "achats.regler", "admin.parametres", "admin.sauvegardes"];

    private static EtatPosteDto PosteOk(DateTime? caisse = null) =>
        new(true, Maintenant.AddHours(-10), "reussie", caisse is null ? null : 4, caisse);

    private static CompteursBarreDto Compteurs(int ventes = 0, int produits = 0, int volets = 0, int deposer = 0, int traites = 0, bool journaux = false) =>
        new(ventes, produits, volets, deposer, traites, journaux);

    private static AffichageBarre Calculer(Func<string, bool>? droits = null, EtatPosteDto? poste = null, EtatLiaisonsPosteDto? liaisons = null,
        CompteursBarreDto? compteurs = null, int? deconnexion = null, string? imprimante = null, bool joignable = true) =>
        PastillesBarre.Calculer(new EtatBarre(joignable, Maintenant, droits ?? Droits(Titulaire), poste ?? PosteOk(), liaisons, compteurs, deconnexion, imprimante));

    private static string[] Libelles(IEnumerable<Pastille> p) => p.Select(x => x.Libelle).ToArray();

    [Fact]
    public void Rien_a_signaler_aucune_pastille()
    {
        var a = Calculer(compteurs: Compteurs(), liaisons: new EtatLiaisonsPosteDto(true, null, 0, null, true, null), deconnexion: 3600);
        Assert.Empty(a.Visibles);
        Assert.False(a.Regroupe);
        // Sauvegarde réussie hier : à jour, rien (seulement en retard ou en échec)
        Assert.Empty(Calculer(poste: new EtatPosteDto(true, Maintenant.AddDays(-1), "reussie", null, null)).Visibles);
    }

    [Fact]
    public void Vendeur_journee_normale_pastilles_grises_avec_lien()
    {
        var a = Calculer(Droits("caisse.vendre"), PosteOk(Maintenant.Date.AddHours(8).AddMinutes(2)), compteurs: Compteurs(ventes: 3, produits: 2));
        Assert.Equal(["Caisse depuis 08:02", "3 ventes en attente", "2 produits revenus"], Libelles(a.Visibles));
        Assert.All(a.Visibles, p => Assert.Null(p.Niveau));
        Assert.Equal(["cloture", "caisse", "patients/attente"], a.Visibles.Select(p => p.Lien ?? "").ToArray());
        Assert.False(a.Regroupe);
    }

    [Fact]
    public void Titulaire_journee_normale_chifa_et_traites()
    {
        var a = Calculer(poste: PosteOk(Maintenant.Date.AddHours(8)), compteurs: Compteurs(ventes: 1, volets: 5, deposer: 12, traites: 2));
        Assert.Equal(["Caisse depuis 08:00", "1 vente en attente", "5 volets Chifa", "12 Chifa à déposer", "2 traites à payer"], Libelles(a.Visibles));
        Assert.Equal(["cloture", "caisse", "chifa", "bordereaux-chifa?rubrique=a-deposer", "achats?onglet=traites"], a.Visibles.Select(p => p.Lien ?? "").ToArray());
    }

    [Fact]
    public void Compteurs_sans_le_droit_rien_et_liens_seulement_avec_le_droit()
    {
        // Compteurs reçus mais droits absents (le serveur filtre déjà ; le poste revérifie)
        var a = Calculer(Droits(), compteurs: Compteurs(3, 2, 5, 12, 2, journaux: true));
        Assert.Empty(a.Visibles);
        // Anomalies visibles par tous mais sans lien pour qui n'a pas le droit de l'écran
        var liaisons = new EtatLiaisonsPosteDto(true, new("Central : erreur d’envoi", "anormal", "d"), 3, null, true, new("2e serveur : décroché", "anormal", "d"));
        a = Calculer(Droits(), new EtatPosteDto(true, Maintenant.AddDays(-1), "echouee", null, null), liaisons, imprimante: "Imprimante introuvable");
        Assert.Equal(["Imprimante tickets : échec", "Central : erreur d’envoi", "2e serveur : décroché", "Sauvegarde échouée"], Libelles(a.Visibles));
        Assert.All(a.Visibles, p => Assert.Null(p.Lien));
        a = Calculer(Droits(Titulaire), new EtatPosteDto(true, Maintenant.AddDays(-1), "echouee", null, null), liaisons, imprimante: "x");
        Assert.Equal(["admin/imprimantes", "admin/synchronisation", "admin/second-serveur", "admin/sauvegardes"], a.Visibles.Select(p => p.Lien ?? "").ToArray());
    }

    [Fact]
    public void Sauvegarde_seulement_en_retard_ou_en_echec()
    {
        Pastille? S(EtatPosteDto p) => Calculer(poste: p).Visibles.SingleOrDefault(x => x.Cle == "sauvegarde");
        Assert.Null(S(new EtatPosteDto(true, Maintenant.AddHours(-2), "reussie", null, null)));
        Assert.Equal(("Sauvegarde en retard", "attention"), S(new EtatPosteDto(true, Maintenant.AddDays(-2), "reussie", null, null)) is { } r ? (r.Libelle, r.Niveau) : default);
        Assert.Equal(("Sauvegarde jamais faite", "attention"), S(new EtatPosteDto(true, null, null, null, null)) is { } j ? (j.Libelle, j.Niveau) : default);
        Assert.Equal(("Sauvegarde désactivée", "attention"), S(new EtatPosteDto(false, Maintenant, "reussie", null, null)) is { } d ? (d.Libelle, d.Niveau) : default);
        Assert.Equal(("Sauvegarde échouée", "anormal"), S(new EtatPosteDto(true, Maintenant, "echouee", null, null)) is { } e ? (e.Libelle, e.Niveau) : default);
        Assert.Equal(("Sauvegarde : disque absent", "anormal"), S(new EtatPosteDto(true, Maintenant, "disque_absent", null, null)) is { } x ? (x.Libelle, x.Niveau) : default);
    }

    [Fact]
    public void Anomalie_affichee_les_grises_se_regroupent_dans_a_traiter()
    {
        var liaisons = new EtatLiaisonsPosteDto(true, new("Central : en attente (2 h 05)", "attention", "d"), 3, null, true, new("2e serveur : décroché", "anormal", "d"));
        var a = Calculer(poste: PosteOk(Maintenant.Date.AddHours(8)), liaisons: liaisons, compteurs: Compteurs(ventes: 3, volets: 5, traites: 2, journaux: true),
            imprimante: "Imprimante introuvable");
        Assert.True(a.Regroupe);
        Assert.Equal(["Imprimante tickets : échec", "Central : en attente (2 h 05)", "2e serveur : décroché", "Journaux : anomalie"], Libelles(a.Visibles));
        Assert.All(a.Visibles, p => Assert.NotNull(p.Niveau));
        Assert.Equal(["Caisse depuis 08:00", "3 ventes en attente", "5 volets Chifa", "2 traites à payer"], Libelles(a.ATraiter));
        Assert.Equal("admin/controle-interne?rubrique=integrite", a.Visibles.Single(p => p.Cle == "journaux").Lien);
        // Date raccourcie quand les pastilles sont regroupées
        Assert.Equal("21/11 · 10:42", PastillesBarre.Date(Maintenant, courte: a.Regroupe));
        Assert.Equal("sam. 21/11 · 10:42", PastillesBarre.Date(Maintenant, courte: false));
    }

    [Fact]
    public void Au_dela_de_4_anomalies_les_rouges_restent_visibles_les_autres_passent_dans_la_liste()
    {
        var liaisons = new EtatLiaisonsPosteDto(true, new("Central : en retard (23 h 53)", "anormal", "d"), 3, null, true, new("2e serveur : en retard (23 h 53)", "attention", "d"));
        var poste = new EtatPosteDto(true, Maintenant.AddDays(-1), "disque_absent", 4, Maintenant.AddDays(-1).Date.AddHours(8));
        var a = Calculer(poste: poste, liaisons: liaisons, compteurs: Compteurs(ventes: 3, journaux: true), deconnexion: 200, imprimante: "x");
        Assert.Equal(PastillesBarre.AnomaliesVisiblesMax, a.Visibles.Count);
        Assert.Equal(["Imprimante tickets : échec", "Central : en retard (23 h 53)", "Sauvegarde : disque absent", "Journaux : anomalie"], Libelles(a.Visibles));
        Assert.Equal(["Déconnexion dans 4 min", "2e serveur : en retard (23 h 53)", "Caisse depuis le 20/11 à 08:00", "3 ventes en attente"], Libelles(a.ATraiter));
        Assert.Equal("attention", a.NiveauGroupe);   // une anomalie rangée dans la liste reste signalée par le bouton

        // Que des anomalies (aucune grise) : regroupées aussi au-delà de 4 ; jusqu'à 4, toutes visibles sans liste
        a = Calculer(poste: poste, liaisons: liaisons, compteurs: Compteurs(journaux: true), imprimante: "x");
        Assert.Equal(4, a.Visibles.Count);
        Assert.Equal(["2e serveur : en retard (23 h 53)", "Caisse depuis le 20/11 à 08:00"], Libelles(a.ATraiter));
        a = Calculer(poste: PosteOk(), liaisons: liaisons, compteurs: Compteurs(journaux: true), imprimante: "x");
        Assert.Equal(4, a.Visibles.Count);
        Assert.False(a.Regroupe);
        Assert.Null(a.NiveauGroupe);
        // Seulement des grises dans la liste : bouton gris
        Assert.Null(Calculer(Droits("caisse.vendre"), PosteOk(Maintenant.AddDays(-1)), compteurs: Compteurs(ventes: 2)).NiveauGroupe);
    }

    [Fact]
    public void Caisse_d_un_autre_jour_orange_et_regroupe_les_grises()
    {
        var a = Calculer(Droits("caisse.vendre"), PosteOk(Maintenant.AddDays(-1).Date.AddHours(8)), compteurs: Compteurs(ventes: 2));
        Assert.Equal(("Caisse depuis le 20/11 à 08:00", "attention"), (a.Visibles[0].Libelle, a.Visibles[0].Niveau));
        Assert.Equal(["2 ventes en attente"], Libelles(a.ATraiter));
    }

    [Fact]
    public void Deconnexion_annoncee_seulement_dans_les_5_dernieres_minutes()
    {
        Assert.DoesNotContain(Calculer(deconnexion: 301).Visibles, p => p.Cle == "deconnexion");
        Assert.DoesNotContain(Calculer(deconnexion: null).Visibles, p => p.Cle == "deconnexion");
        Assert.DoesNotContain(Calculer(deconnexion: 0).Visibles, p => p.Cle == "deconnexion");
        var d = Calculer(Droits(), deconnexion: 4 * 60 - 10).Visibles.Single();
        Assert.Equal(("Déconnexion dans 4 min", "attention", (string?)null), (d.Libelle, d.Niveau, d.Lien));
        Assert.Equal("Déconnexion dans 5 min", Calculer(deconnexion: 300).Visibles.Single().Libelle);
        Assert.Equal("Déconnexion dans 1 min", Calculer(deconnexion: 20).Visibles.Single().Libelle);
    }

    [Fact]
    public void Imprimante_en_echec_visible_par_tous_vendeurs_compris()
    {
        foreach (var droits in new[] { Droits(), Droits("caisse.vendre"), Droits(Titulaire) })
        {
            var p = Calculer(droits, imprimante: "Impression impossible : vérifiez que l’imprimante est allumée et branchée.").Visibles.Single();
            Assert.Equal(("imprimante", "anormal"), (p.Cle, p.Niveau));
            Assert.Contains("vérifiez que l’imprimante est allumée", p.Titre);
        }
        Assert.Empty(Calculer(imprimante: null).Visibles);
        Assert.Empty(Calculer(imprimante: "").Visibles);
    }

    [Fact]
    public void Serveur_injoignable_seules_restent_les_pastilles_qui_ne_dependent_pas_du_serveur()
    {
        var liaisons = new EtatLiaisonsPosteDto(true, new("Central : erreur d’envoi", "anormal", "d"), 3, null, false, null);
        var a = Calculer(poste: new EtatPosteDto(true, null, "echouee", 4, Maintenant.Date), liaisons: liaisons,
            compteurs: Compteurs(3, 2, 5, 12, 2, journaux: true), deconnexion: 120, imprimante: "Imprimante introuvable", joignable: false);
        Assert.Equal(["Déconnexion dans 2 min", "Imprimante tickets : échec"], Libelles(a.Visibles));
        Assert.False(a.Regroupe);
    }

    [Fact]
    public void Niveau_de_liaison_inconnu_ramene_a_attention()
    {
        var a = Calculer(liaisons: new EtatLiaisonsPosteDto(true, new("Central : x", "rouge", "d"), 0, null, false, null));
        Assert.Equal("attention", a.Visibles.Single().Niveau);
    }

    [Fact]
    public void Identite_poste_et_vendeur_sinon_vendeur_seul()
    {
        Assert.Equal("Caisse 1 · Karim", PastillesBarre.Identite("Caisse 1", "Karim"));
        Assert.Equal("Karim", PastillesBarre.Identite(null, "Karim"));
        Assert.Equal("Caisse 1", PastillesBarre.Identite(" Caisse 1 ", null));
        Assert.Equal("", PastillesBarre.Identite(null, null));
    }
}
