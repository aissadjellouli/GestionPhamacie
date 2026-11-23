using System.Net;
using System.Net.Http;
using Pharma.Desktop.Services;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Bascule du poste sur le 2e serveur (lot V2-6) : adresse courante sans redémarrage, vigie (rapport, consigne), 503 « serveur_ecarte »
/// (bascule, indécis, isolé), échange poste.json (Q27), repli 401 (Q26), relais (Q24) et officine à un seul serveur (inchangée).
/// Le réseau, la session et poste.json sont des doublures : aucun fichier ni serveur réel.
/// </summary>
public class BasculePosteTests
{
    private static readonly Uri Serveur1 = new("http://s1:5080/");
    private static readonly Uri Secours = new("http://s2:5080/");

    // ---- Doublures ----
    private sealed class FauxReglages : IReglagesVigie
    {
        public ReglagesPoste.Valeurs Actuelles { get; private set; } = new() { AdresseApi = "http://s1:5080/", AdresseApiSecours = "http://s2:5080/" };
        public int Ecritures { get; private set; }
        public string? Erreur { get; set; }
        public string? Enregistrer(ReglagesPoste.Valeurs valeurs) { Ecritures++; if (Erreur is null) Actuelles = valeurs; return Erreur; }
    }

    private sealed class FausseSession : ISessionVigie
    {
        public bool EstConnecte { get; set; }
        public string? Jeton { get; set; }
        public string? MotifFermeture { get; private set; }
        public void FermerPourRepli(string motif) { MotifFermeture = motif; EstConnecte = false; Jeton = null; }
    }

    private sealed class FauxReseau : IReseauVigie
    {
        public Dictionary<Uri, EtatHealthcheck> Etats { get; } = new()
        {
            [Serveur1] = new(true, false, RoleServeur.Principal),
            [Secours] = new(true, false, RoleServeur.Veille)
        };
        public string Consigne { get; set; } = ConsigneVigie.Rester;
        public bool SecoursRepond { get; set; } = true;
        public bool? JetonAccepte { get; set; } = true;
        public List<RapportPosteDto> Rapports { get; } = [];
        public List<string?> Jetons { get; } = [];
        public int Sondes { get; private set; }

        public Task<EtatHealthcheck> SonderAsync(Uri adresse, CancellationToken ct)
        {
            Sondes++;
            return Task.FromResult(Etats.TryGetValue(adresse, out var e) ? e : new(false, false, null));
        }

        public Task<ConsigneVigieDto?> RapporterAsync(Uri secours, RapportPosteDto rapport, string? jeton, CancellationToken ct)
        {
            Rapports.Add(rapport);
            Jetons.Add(jeton);
            return Task.FromResult<ConsigneVigieDto?>(SecoursRepond ? new(RoleServeur.Veille, Consigne, null) : null);
        }

        public Task<bool?> JetonAccepteAsync(Uri adresse, string jeton, CancellationToken ct) => Task.FromResult(JetonAccepte);
    }

    private sealed class Banc
    {
        public FauxReglages Reglages { get; } = new();
        public FausseSession Session { get; } = new();
        public FauxReseau Reseau { get; } = new();
        public PanierBascule Panier { get; } = new();
        public AdresseApiCourante Adresse { get; } = new(Serveur1);
        public DateTime Maintenant { get; set; } = new(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc);
        public VigiePoste Vigie { get; }
        public Banc() => Vigie = new VigiePoste(Reglages, Session, Adresse, Reseau, Panier, "Poste caisse 1", null, "2026.11", () => Maintenant);
    }

    private static void SecoursDevientPrincipal(Banc b) => b.Reseau.Etats[Secours] = new(true, false, RoleServeur.Principal);
    private static void Serveur1Tombe(Banc b) => b.Reseau.Etats[Serveur1] = new(false, false, null);

    // ---- Décisions pures ----
    [Fact]
    public void Case_cochee_par_defaut_des_qu_une_adresse_de_secours_existe()
    {
        Assert.False(BasculeSecours.Automatique(new() { AdresseApi = "http://s1:5080/" }));
        Assert.True(BasculeSecours.Automatique(new() { AdresseApiSecours = "http://s2:5080/" }));
        Assert.False(BasculeSecours.Automatique(new() { AdresseApiSecours = "http://s2:5080/", BasculeAutomatique = false }));
        Assert.False(BasculeSecours.Automatique(new() { AdresseApiSecours = "pas une adresse", BasculeAutomatique = true }));
    }

    [Theory]
    [InlineData(MotifEcartServeur.Bascule, true, ActionBascule.Basculer)]
    [InlineData(MotifEcartServeur.Bascule, false, ActionBascule.Aucune)]
    [InlineData(MotifEcartServeur.TimelinesEgales, true, ActionBascule.Indecis)]
    [InlineData(MotifEcartServeur.Isole, true, ActionBascule.Isole)]
    [InlineData(MotifEcartServeur.Isole, false, ActionBascule.Isole)]
    [InlineData("motif_inconnu_d_un_futur_serveur", true, ActionBascule.Indecis)]
    [InlineData(null, true, ActionBascule.Indecis)]
    public void Refus_serveur_ecarte_selon_le_motif(string? motif, bool automatique, ActionBascule attendu) =>
        Assert.Equal(attendu, BasculeSecours.PourEcart(motif, automatique));

    [Theory]
    [InlineData(ConsigneVigie.Rester, true, ActionBascule.Aucune)]
    [InlineData(ConsigneVigie.Attendre, true, ActionBascule.Attendre)]
    [InlineData(ConsigneVigie.Relais, true, ActionBascule.Relais)]
    [InlineData(ConsigneVigie.Basculer, true, ActionBascule.Basculer)]
    [InlineData(ConsigneVigie.Basculer, false, ActionBascule.Aucune)]
    [InlineData("inconnue", true, ActionBascule.Aucune)]
    public void Consigne_du_2e_serveur(string consigne, bool automatique, ActionBascule attendu) =>
        Assert.Equal(attendu, BasculeSecours.PourConsigne(consigne, automatique));

    [Theory]
    [InlineData(null, true)]
    [InlineData("principal", true)]
    [InlineData("veille", false)]
    [InlineData("ecarte", false)]
    public void Seul_un_serveur_principal_est_une_cible(string? role, bool attendu) => Assert.Equal(attendu, BasculeSecours.EstPrincipal(role));

    [Fact]
    public void Echange_des_deux_adresses_garde_les_autres_reglages()
    {
        var v = new ReglagesPoste.Valeurs { AdresseApi = "http://s1:5080/", AdresseApiSecours = "http://s2:5080/", ImprimanteTickets = "Tickets", BasculeAutomatique = false };
        var apres = BasculeSecours.Echanger(v, "http://s1:5080/");
        Assert.Equal("http://s2:5080/", apres.AdresseApi);
        Assert.Equal("http://s1:5080/", apres.AdresseApiSecours);
        Assert.Equal("Tickets", apres.ImprimanteTickets);
        Assert.False(apres.BasculeAutomatique);
    }

    // ---- Adresse courante et relais HTTP ----
    [Fact]
    public void Requete_reecrite_vers_l_adresse_courante_chemin_et_requete_gardes()
    {
        var r = AdresseApiCourante.Reecrire(new Uri("http://s1:5080/api/produits?q=a%20b"), Serveur1, new Uri("https://s2:5443/"));
        Assert.Equal("https://s2:5443/api/produits?q=a%20b", r.AbsoluteUri);
        Assert.Equal(new Uri("http://s1:5080/api/x"), AdresseApiCourante.Reecrire(new Uri("http://s1:5080/api/x"), Serveur1, Serveur1));
    }

    [Fact]
    public void Requete_reecrite_avec_un_prefixe_de_chemin()
    {
        var r = AdresseApiCourante.Reecrire(new Uri("http://s1/pharma/api/x"), new Uri("http://s1/pharma/"), new Uri("http://s2/officine/"));
        Assert.Equal("http://s2/officine/api/x", r.ToString());
    }

    private sealed class Capteur : HttpMessageHandler
    {
        public List<Uri> Vues { get; } = [];
        public HttpResponseMessage Reponse { get; set; } = new(HttpStatusCode.OK);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Vues.Add(r.RequestUri!); return Task.FromResult(Reponse); }
    }

    [Fact]
    public async Task Le_relais_change_d_hote_sans_recreer_le_client()
    {
        var adresse = new AdresseApiCourante(Serveur1);
        var capteur = new Capteur();
        using var client = new HttpClient(new RelaisAdresse(adresse, capteur)) { BaseAddress = Serveur1 };
        await client.GetAsync("api/healthcheck");
        adresse.Changer(Secours);
        await client.GetAsync("api/healthcheck");
        Assert.Equal(["http://s1:5080/api/healthcheck", "http://s2:5080/api/healthcheck"], capteur.Vues.Select(u => u.ToString()));
    }

    private sealed class Ecoute : IEcouteEcart
    {
        public List<string?> Motifs { get; } = [];
        public Task EcartRecuAsync(string? motif) { Motifs.Add(motif); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Un_503_serveur_ecarte_est_confie_a_la_vigie_et_son_corps_reste_lisible()
    {
        var capteur = new Capteur
        {
            Reponse = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("{\"code\":\"serveur_ecarte\",\"motif\":\"bascule\",\"serveurRetenu\":\"s2\"}")
            }
        };
        var ecoute = new Ecoute();
        using var client = new HttpClient(new SurveillanceEcart(ecoute, capteur)) { BaseAddress = Serveur1 };
        var rep = await client.GetAsync("api/produits");
        Assert.Equal(["bascule"], ecoute.Motifs);
        Assert.Contains("serveur_ecarte", await rep.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "{\"code\":\"serveur_en_veille\"}")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "pas du json")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "")]
    public async Task Les_autres_reponses_ne_declenchent_rien(HttpStatusCode code, string corps)
    {
        var ecoute = new Ecoute();
        using var client = new HttpClient(new SurveillanceEcart(ecoute, new Capteur { Reponse = new(code) { Content = new StringContent(corps) } })) { BaseAddress = Serveur1 };
        await client.GetAsync("api/produits");
        Assert.Empty(ecoute.Motifs);
    }

    // ---- Un seul serveur : comportement actuel inchange ----
    [Fact]
    public async Task Un_seul_serveur_aucun_rapport_aucune_sonde_aucune_bascule()
    {
        var b = new Banc();
        b.Reglages.Actuelles.AdresseApiSecours = null;
        await b.Vigie.EtapeAsync();
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Bascule);
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Isole);
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.TimelinesEgales);
        Assert.False(b.Vigie.Active);
        Assert.Empty(b.Reseau.Rapports);
        Assert.Equal(0, b.Reseau.Sondes);
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
        Assert.False(b.Vigie.VentesSuspendues);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(0, b.Reglages.Ecritures);
        Assert.Equal(0, b.Vigie.Generation);
    }

    // ---- Vigie : rapport ----
    [Fact]
    public async Task Rapport_a_chaque_etape_avec_le_jeton_de_l_employe_connecte()
    {
        var b = new Banc();
        b.Session.EstConnecte = true;
        b.Session.Jeton = "jeton-1";
        await b.Vigie.EtapeAsync();
        var r = Assert.Single(b.Reseau.Rapports);
        Assert.Equal(new RapportPosteDto("Poste caisse 1", true, null, "2026.11", "http://s1:5080/"), r);
        Assert.Equal("jeton-1", b.Reseau.Jetons[0]);
        Assert.Equal(TimeSpan.FromSeconds(15), b.Vigie.Cadence);
    }

    [Fact]
    public async Task Rapport_sans_jeton_quand_personne_n_est_connecte_et_cadence_de_5_s_apres_un_echec()
    {
        var b = new Banc();
        Serveur1Tombe(b);
        await b.Vigie.EtapeAsync();
        b.Maintenant = b.Maintenant.AddSeconds(5);
        await b.Vigie.EtapeAsync();
        Assert.Null(b.Reseau.Jetons[0]);
        Assert.False(b.Reseau.Rapports[1].Serveur1Joint);
        Assert.Equal(new DateTime(2026, 11, 23, 10, 0, 0, DateTimeKind.Utc), b.Reseau.Rapports[1].EchecDepuisUtc);   // début de la perte, pas la date du rapport
        Assert.Equal(TimeSpan.FromSeconds(5), b.Vigie.Cadence);
    }

    [Fact]
    public async Task Case_decochee_les_rapports_partent_mais_aucune_bascule_automatique()
    {
        var b = new Banc();
        b.Reglages.Actuelles.BasculeAutomatique = false;
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        await b.Vigie.EtapeAsync();
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Bascule);
        Assert.Single(b.Reseau.Rapports);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
        Assert.Equal(0, b.Reglages.Ecritures);
    }

    // ---- Consignes ----
    [Fact]
    public async Task Consigne_attendre_puis_rester_bandeau_d_attente_puis_retour_normal()
    {
        var b = new Banc();
        b.Reseau.Consigne = ConsigneVigie.Attendre;
        await b.Vigie.EtapeAsync();
        Assert.Equal(SituationBascule.Attente, b.Vigie.Situation);
        Assert.Equal(BasculeSecours.MessageAttente, b.Vigie.Message);
        b.Reseau.Consigne = ConsigneVigie.Rester;
        await b.Vigie.EtapeAsync();
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
    }

    [Fact]
    public async Task Consigne_basculer_vers_un_2e_serveur_principal_sans_deconnexion_ni_redemarrage()
    {
        var b = new Banc();
        b.Session.EstConnecte = true;
        b.Session.Jeton = "jeton";
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        var leve = 0;
        b.Vigie.Change += () => leve++;
        await b.Vigie.EtapeAsync();
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.Equal(SituationBascule.Basculee, b.Vigie.Situation);
        Assert.Equal(BasculeSecours.MessageBascule, b.Vigie.Message);
        Assert.True(b.Vigie.SurSecours);
        Assert.Equal(1, b.Vigie.Generation);
        Assert.True(b.Session.EstConnecte);
        Assert.False(b.Panier.EnAttente);
        Assert.True(leve > 0);
        // Q27 : adresses échangées
        Assert.Equal("http://s2:5080/", b.Reglages.Actuelles.AdresseApi);
        Assert.Equal("http://s1:5080/", b.Reglages.Actuelles.AdresseApiSecours);
    }

    [Fact]
    public async Task Consigne_basculer_mais_2e_serveur_en_veille_aucune_bascule()
    {
        var b = new Banc();
        b.Reseau.Consigne = ConsigneVigie.Basculer;   // le serveur 2 annonce « veille » au healthcheck
        await b.Vigie.EtapeAsync();
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(0, b.Vigie.Generation);
        Assert.Equal(SituationBascule.Attente, b.Vigie.Situation);
        Assert.Equal(0, b.Reglages.Ecritures);
    }

    [Fact]
    public async Task Jamais_de_retour_automatique_vers_l_ancien_serveur()
    {
        var b = new Banc();
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        await b.Vigie.EtapeAsync();
        b.Reseau.Consigne = ConsigneVigie.Rester;
        b.Reseau.Etats[Serveur1] = new(true, false, RoleServeur.Principal);   // l'ancien serveur répond de nouveau
        for (var i = 0; i < 20; i++) { b.Maintenant = b.Maintenant.AddMinutes(1); await b.Vigie.EtapeAsync(); }
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.Equal(1, b.Vigie.Generation);
        Assert.Equal("http://s2:5080/", b.Reglages.Actuelles.AdresseApi);
    }

    [Fact]
    public async Task Reglages_non_enregistres_la_bascule_a_lieu_et_le_message_le_dit()
    {
        var b = new Banc();
        b.Reglages.Erreur = "disque plein";
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        await b.Vigie.EtapeAsync();
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.EndsWith(BasculeSecours.MessageReglagesNonEnregistres, b.Vigie.Message);
    }

    // ---- Repli 401 (Q26) ----
    [Fact]
    public async Task Jeton_refuse_par_le_nouveau_serveur_repli_ecran_de_connexion_et_panier_demande()
    {
        var b = new Banc();
        b.Session.EstConnecte = true;
        b.Session.Jeton = "jeton";
        b.Reseau.JetonAccepte = false;
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        var demandes = 0;
        b.Panier.Abonner(() => { demandes++; b.Panier.Garder("panier"); return Task.CompletedTask; });
        await b.Vigie.EtapeAsync();
        Assert.Equal(1, demandes);
        Assert.Equal(SituationBascule.Repli, b.Vigie.Situation);
        Assert.Equal(BasculeSecours.MessageRepli, b.Session.MotifFermeture);
        Assert.False(b.Session.EstConnecte);
        Assert.Equal(Secours, b.Adresse.Actuelle);
        // Rendu une seule fois, en mémoire
        Assert.Equal("panier", b.Panier.Reprendre<string>());
        Assert.Null(b.Panier.Reprendre<string>());
        Assert.False(b.Panier.EnAttente);
    }

    [Fact]
    public async Task Jeton_indetermine_pas_de_deconnexion()
    {
        var b = new Banc();
        b.Session.EstConnecte = true;
        b.Session.Jeton = "jeton";
        b.Reseau.JetonAccepte = null;
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        await b.Vigie.EtapeAsync();
        Assert.True(b.Session.EstConnecte);
        Assert.Equal(SituationBascule.Basculee, b.Vigie.Situation);
    }

    // ---- Relais (Q24) ----
    [Fact]
    public async Task Relais_les_appels_passent_par_le_2e_serveur_puis_retour_direct_apres_2_minutes_de_serveur_1_joint()
    {
        var b = new Banc();
        Serveur1Tombe(b);
        b.Reseau.Consigne = ConsigneVigie.Relais;
        await b.Vigie.EtapeAsync();
        Assert.Equal(SituationBascule.Relais, b.Vigie.Situation);
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.True(b.Vigie.EnRelais);
        Assert.Equal(0, b.Vigie.Generation);   // pas une bascule : poste.json intact
        Assert.Equal(0, b.Reglages.Ecritures);

        b.Reseau.Etats[Serveur1] = new(true, false, RoleServeur.Principal);
        b.Maintenant = b.Maintenant.AddSeconds(15);
        await b.Vigie.EtapeAsync();
        Assert.Equal(Secours, b.Adresse.Actuelle);   // moins de 2 minutes
        b.Maintenant = b.Maintenant.AddSeconds(ReglagesBasculeParDefaut.RetourVoieDirecteSecondes);
        b.Reseau.Consigne = ConsigneVigie.Rester;
        await b.Vigie.EtapeAsync();
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
    }

    // ---- 503 serveur_ecarte ----
    [Fact]
    public async Task Ecart_bascule_vers_le_secours_principal_pas_vers_serveurRetenu()
    {
        var b = new Banc();
        SecoursDevientPrincipal(b);
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Bascule);
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.Equal(1, b.Vigie.Generation);
        Assert.Equal(SituationBascule.Basculee, b.Vigie.Situation);
    }

    [Fact]
    public async Task Ecart_bascule_secours_en_veille_attente_puis_bascule_des_que_il_est_principal()
    {
        var b = new Banc();
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Bascule);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(SituationBascule.Attente, b.Vigie.Situation);
        b.Reseau.Etats[Serveur1] = new(true, true, RoleServeur.Ecarte);   // le serveur 1 répond au healthcheck mais reste écarté
        await b.Vigie.EtapeAsync();
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        SecoursDevientPrincipal(b);
        await b.Vigie.EtapeAsync();
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.Equal(SituationBascule.Basculee, b.Vigie.Situation);
    }

    [Fact]
    public async Task Ecart_timelines_egales_pas_de_bascule_bandeau_indecis_et_reprise_quand_le_serveur_repond_normalement()
    {
        var b = new Banc();
        SecoursDevientPrincipal(b);   // même un secours principal ne change rien : « timelines_egales » n'est pas une bascule
        b.Reseau.Etats[Serveur1] = new(true, true, RoleServeur.Ecarte);
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.TimelinesEgales);
        await b.Vigie.EtapeAsync();
        Assert.Equal(SituationBascule.Indecis, b.Vigie.Situation);
        Assert.Equal(BasculeSecours.MessageIndecis, b.Vigie.Message);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(0, b.Vigie.Generation);
        b.Reseau.Etats[Serveur1] = new(true, false, RoleServeur.Principal);
        await b.Vigie.EtapeAsync();
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
    }

    [Fact]
    public async Task Ecart_isole_ventes_suspendues_message_exact_aucune_bascule_meme_avec_consigne_basculer()
    {
        var b = new Banc();
        SecoursDevientPrincipal(b);
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        b.Reseau.Etats[Serveur1] = new(true, true, RoleServeur.Ecarte);
        b.Session.EstConnecte = true;
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Isole);
        Assert.True(b.Vigie.VentesSuspendues);
        Assert.Equal("Ce poste est coupé du réseau de la pharmacie. Les ventes sont suspendues sur ce poste. Prévenez le titulaire.", b.Vigie.Message);
        await b.Vigie.EtapeAsync();   // plus de 503 en boucle : sonde toutes les 15 s, pas de relance
        Assert.True(b.Vigie.VentesSuspendues);
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        Assert.Equal(0, b.Vigie.Generation);
        Assert.True(b.Session.EstConnecte);   // ni déconnexion ni perte du panier
        Assert.False(b.Panier.EnAttente);
        // Reprise dès que le serveur 1 répond normalement (le 2e serveur ne demande plus de basculer)
        b.Reseau.Consigne = ConsigneVigie.Rester;
        b.Reseau.Etats[Serveur1] = new(true, false, RoleServeur.Principal);
        await b.Vigie.EtapeAsync();
        Assert.False(b.Vigie.VentesSuspendues);
        Assert.Equal(SituationBascule.Normale, b.Vigie.Situation);
    }

    [Fact]
    public async Task Ecart_isole_meme_case_decochee_les_ventes_sont_suspendues()
    {
        var b = new Banc();
        b.Reglages.Actuelles.BasculeAutomatique = false;
        await b.Vigie.EcartRecuAsync(MotifEcartServeur.Isole);
        Assert.True(b.Vigie.VentesSuspendues);
    }

    // ---- Bouton manuel ----
    [Fact]
    public async Task Bouton_manuel_refuse_un_2e_serveur_en_veille_et_accepte_un_principal()
    {
        var b = new Banc();
        b.Reglages.Actuelles.BasculeAutomatique = false;
        Assert.Equal(BasculeSecours.MessageSecoursPasPrincipal, await b.Vigie.BasculerManuellementAsync());
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
        SecoursDevientPrincipal(b);
        Assert.Null(await b.Vigie.BasculerManuellementAsync());
        Assert.Equal(Secours, b.Adresse.Actuelle);
        Assert.Equal("http://s2:5080/", b.Reglages.Actuelles.AdresseApi);
    }

    [Fact]
    public async Task Bouton_manuel_secours_injoignable()
    {
        var b = new Banc();
        b.Reseau.Etats[Secours] = new(false, false, null);
        Assert.Equal("Le 2e serveur ne répond pas.", await b.Vigie.BasculerManuellementAsync());
        Assert.Equal(Serveur1, b.Adresse.Actuelle);
    }

    // ---- Encaissement interrompu (Q25) : la generation permet à ApiClient de renvoyer une fois ----
    [Fact]
    public async Task Apres_un_echec_reseau_la_generation_change_si_la_bascule_a_lieu()
    {
        var b = new Banc();
        var avant = b.Vigie.Generation;
        Serveur1Tombe(b);
        b.Reseau.Consigne = ConsigneVigie.Basculer;
        SecoursDevientPrincipal(b);
        await b.Vigie.ApresEchecAsync();
        Assert.NotEqual(avant, b.Vigie.Generation);
    }

    [Fact]
    public async Task Un_seul_serveur_apres_un_echec_rien_ne_change()
    {
        var b = new Banc();
        b.Reglages.Actuelles.AdresseApiSecours = null;
        await b.Vigie.ApresEchecAsync();
        Assert.Equal(0, b.Vigie.Generation);
        Assert.Empty(b.Reseau.Rapports);
    }

    // ---- Panier en memoire ----
    [Fact]
    public void Le_panier_gardé_n_existe_qu_en_memoire_et_rend_le_bon_type_une_seule_fois()
    {
        var p = new PanierBascule();
        p.Garder(new List<int> { 1, 2 });
        Assert.Null(p.Reprendre<string>());
        Assert.True(p.EnAttente);
        Assert.Equal([1, 2], p.Reprendre<List<int>>());
        Assert.False(p.EnAttente);
        p.Garder("x");
        p.Effacer();
        Assert.Null(p.Reprendre<string>());
    }
}
