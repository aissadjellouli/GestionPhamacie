namespace Pharma.Architecture.Tests;

/// <summary>
/// Bascule du poste sur le 2e serveur (lot V2-6) : câblage du poste Blazor Hybrid WPF vérifié sur les sources (la logique est testée
/// dans Pharma.Application.Tests/BasculePosteTests). Barre d'état, caisse, connexion, réglages du poste, client HTTP.
/// </summary>
public class BasculePosteCablageTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", .. chemin]));

    [Fact]
    public void Client_http_adresse_courante_changeable_et_503_confies_a_la_vigie()
    {
        var app = Lire("App.xaml.cs");
        // Liste des postes autorisés : chaque requête est signée par le poste, juste avant l'envoi (après la réécriture de l'adresse)
        Assert.Contains("new RelaisAdresse(adresseCourante, new SignaturePoste(identite, horlogeServeur, new SuiviAppels(appels)))", app);
        Assert.Contains("new SurveillanceEcart(vigie,", app);
        Assert.Contains("vigie.Demarrer();", app);
        var api = Lire("Services", "ApiClient.cs");
        Assert.Contains("(adresseCourante?.Actuelle ?? http.BaseAddress)", api);
        Assert.DoesNotContain("http.BaseAddress?.ToString()", api);
    }

    [Fact]
    public void Encaissement_interrompu_renvoye_une_fois_avec_la_meme_commande_donc_la_meme_cle()
    {
        var api = Lire("Services", "ApiClient.cs");
        var debut = api.IndexOf("public async Task<(VenteEncaisseeDto?, string?)> EncaisserAsync", StringComparison.Ordinal);
        var corps = api[debut..][..api[debut..].IndexOf("\n    }", StringComparison.Ordinal)];
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(corps, @"PostAsync<CommandeVenteDto, VenteEncaisseeDto>\(""api/caisse/encaisser"", commande\)").Count);
        Assert.Contains("bascule is not { Active: true }", corps);   // un seul serveur : aucun renvoi
    }

    [Fact]
    public void Encaissement_en_suspens_renvoye_par_la_caisse_apres_la_bascule_avec_la_commande_gardee()
    {
        // Essai réel du 2026-11-25 : promotion 108 s après la panne, la tentative immédiate d'ApiClient ne suffisait pas
        var caisse = Lire("Composants", "Pages", "Caisse.razor");
        Assert.Contains("var panne = err == ApiClient.Injoignable || err == ApiClient.MessageDelaiDepasse;", caisse);
        Assert.Contains("RenvoiEncaissement.APlacer(Bascule.Active, panne)", caisse);
        Assert.Contains("await Api.EncaisserAsync(r.Commande);", caisse);   // la commande du premier envoi, donc la même clé
        Assert.Contains("r.Etape(DateTime.UtcNow, Bascule.Generation, joint, Session.EstConnecte, Bascule.VentesSuspendues)", caisse);
        // Panier et paiement figés pendant le suspens ; Encaisser (bouton et F12) refusé ; renvoi arrêté en quittant la Caisse
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(caisse, @"inert=""@\(renvoi is not null\)""").Count);
        Assert.Contains("private bool PeutEncaisser => !enCours && renvoi is null", caisse);
        Assert.Contains("ArreterRenvoi();\n        Historique.Oublier(\"caisse\");", caisse.Replace("\r\n", "\n"));
        Assert.Contains("Annuler le renvoi automatique", caisse);
    }

    [Fact]
    public void Panier_a_la_cle_en_doute_jamais_mis_en_attente_garde_en_memoire_avec_sa_cle()
    {
        // Décision du titulaire (2026-11-25) : pas de mise en attente pendant la panne (une reprise changerait la clé : double possible)
        var caisse = Lire("Composants", "Pages", "Caisse.razor").Replace("\r\n", "\n");
        Assert.Contains("if (cleEnDoute) { erreurAttente = MessageAttenteRefusee; return; }", caisse);   // garde unique de toute mise en attente
        Assert.Contains("if (panne) cleEnDoute = true;", caisse);
        Assert.Contains("disabled=\"@(enCours || lignes.Count == 0 || cleEnDoute)\"", caisse);           // bouton ⏸ Attente
        // Quitter la Caisse, changer d'utilisateur, déconnexion, écran détruit : gardé en mémoire avec sa clé, jamais en attente
        Assert.Contains("if (cleEnDoute) { GarderPanierEnDoute(); return true; }", caisse);
        Assert.Contains("if (cleEnDoute) { GarderPanierEnDoute(); await InvokeAsync(StateHasChanged); return null; }", caisse);
        Assert.Contains("if (cleEnDoute && lignes.Any(l => l.Quantite > 0)) { GarderPanierEnDoute(); return; }", caisse);
        Assert.Contains("if (cleEnDoute) { GarderPanierEnDoute(); return; }", caisse);
        Assert.Contains("Panier.Garder(new PanierCapture([.. lignes], cleEncaissement, patient, mode, remisePct, CleEnDoute: true));", caisse);
        // Rendu avec sa clé et son doute ; un nouveau panier (vente réussie, panier vidé) lève le doute
        Assert.Contains("(cleEncaissement, patient, mode, remisePct, cleEnDoute) = (p.Cle, p.Patient, p.Mode, p.RemisePct, p.CleEnDoute);", caisse);
        Assert.Contains("cleEncaissement = Guid.NewGuid();\n        cleEnDoute = false;", caisse);
    }

    [Fact]
    public void Barre_d_etat_bandeaux_de_la_vigie_et_bouton_manuel_sans_redemarrage_avec_role_verifie()
    {
        var barre = Lire("Composants", "Mise", "BarreEtat.razor");
        Assert.Contains("@inject VigiePoste Bascule", barre);
        Assert.Contains("class=\"bandeau-attention\"", barre);
        Assert.Contains("Bascule.BasculerManuellementAsync()", barre);
        Assert.DoesNotContain("fermez puis rouvrez", barre);
        Assert.Contains("Connecté · par le 2e serveur (relais)", barre);
        Assert.Contains("Connecté · 2e serveur", barre);
        Assert.Contains("Bascule.Change -= SurBascule;", barre);
    }

    [Fact]
    public void Caisse_ventes_suspendues_si_poste_isole_et_panier_garde_en_memoire_seulement()
    {
        var caisse = Lire("Composants", "Pages", "Caisse.razor");
        Assert.Contains("Bascule.VentesSuspendues ? BasculeSecours.MessageIsole", caisse);
        Assert.Contains("if (!PeutEncaisser || Bascule.VentesSuspendues) return;", caisse);
        Assert.Contains("Panier.Reprendre<PanierCapture>()", caisse);
        Assert.Contains("Panier.Desabonner(MettreDeCoteBasculeAsync);", caisse);
        var panier = Lire("Services", "PanierBascule.cs");
        Assert.DoesNotContain("System.IO", panier);
        Assert.DoesNotContain("File.", panier);
        Assert.Equal("Ce poste est coupé du réseau de la pharmacie. Les ventes sont suspendues sur ce poste. Prévenez le titulaire.",
            Pharma.Shared.MessagesBasculeVague2.PosteIsole);
    }

    [Fact]
    public void Case_basculer_automatiquement_dans_les_reglages_du_poste()
    {
        var admin = Lire("Composants", "Pages", "AdminImprimantes.razor");
        Assert.Contains("data-champ=\"bascule_automatique\"", admin);
        Assert.Contains("Basculer automatiquement sur le 2e serveur", admin);
        Assert.Contains("BasculeAutomatique = basculeAuto ? null : false", admin);
        Assert.Contains("public bool? BasculeAutomatique { get; set; }", Lire("Services", "ReglagesPoste.cs"));
    }

    [Fact]
    public void Connexion_rend_la_caisse_a_l_employe_qui_se_reconnecte_apres_un_repli()
    {
        var connexion = Lire("Composants", "Pages", "Connexion.razor");
        Assert.Contains("(Session.AVenteAReprendre || PanierARendre)", connexion);
    }
}
