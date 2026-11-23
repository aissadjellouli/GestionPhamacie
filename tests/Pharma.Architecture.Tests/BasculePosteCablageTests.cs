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
        Assert.Contains("new RelaisAdresse(adresseCourante, new SuiviAppels(appels))", app);
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
