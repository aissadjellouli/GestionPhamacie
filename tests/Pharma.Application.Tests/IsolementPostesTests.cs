using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>
/// Vague 2, lot V2-1b : présence des postes (registre en mémoire, écriture limitée) et auto-mise à l'écart du serveur 1 isolé (Q32), horloge simulée.
/// </summary>
public class IsolementPostesTests
{
    // ---- Doublures ----

    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc { get; private set; } = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
        public void Avancer(double secondes) => MaintenantUtc += TimeSpan.FromSeconds(secondes);
    }

    private sealed class Releves : IReleveServeurs
    {
        public bool Regle { get; set; } = true;
        public ReleveServeur? Local { get; set; }
        public ReleveServeur? Autre { get; set; }
        public bool AutreServeurRegle => Regle;
        public string? AutreServeur => "AUTRE";
        public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) => Task.FromResult(Local);
        public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) => Task.FromResult(Autre);
    }

    private sealed class Copie : ICopieConnectee
    {
        public bool? Connectee { get; set; } = false;
        public Task<bool?> ConnecteeAsync(CancellationToken ct = default) => Task.FromResult(Connectee);
    }

    /// <summary>Postes connus de la base du serveur 1 : référence = heure courante, chacun vu il y a 10 s (ou rien si <see cref="Vide"/>).</summary>
    private sealed class Depot(Horloge horloge) : IPostesConnusDepot
    {
        public List<string> Connus { get; } = [];
        public bool Vide { get; set; }
        public Task<PostesConnusLus> LireAsync(CancellationToken ct = default) =>
            Task.FromResult(new PostesConnusLus(horloge.MaintenantUtc, Vide ? [] : Connus.Select(p => new PosteVuEnBase(p, horloge.MaintenantUtc.AddSeconds(-10))).ToList()));
    }

    private sealed class Autre : ILecteurEtatAutreServeur
    {
        public EtatAutreServeurLu Etat { get; set; } = new(true, "veille", true);
        public Task<EtatAutreServeurLu> LireAsync(CancellationToken ct = default) => Task.FromResult(Etat);
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public List<string> Messages { get; } = [];
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task SecondServeurAsync(string objet, string detail, string sms, CancellationToken ct = default) { Messages.Add(objet); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Banc
    {
        public Horloge H { get; } = new();
        public Releves Releves { get; } = new();
        public Copie Copie { get; } = new();
        public Depot Depot { get; }
        public RegistrePostesVus Vus { get; } = new();
        public Autre Autre { get; } = new();
        public SuiviServeurEcarte Suivi { get; } = new();
        public Alertes Alertes { get; } = new();
        public ReglagesBascule Reglages { get; } = new ReglagesBascule().Borner();
        public SurveillerIsolement Surveiller { get; }
        public List<string> EnLigne { get; } = [];

        public Banc(bool veille = false, bool passerLaGrace = true)
        {
            Depot = new Depot(H);
            Surveiller = new SurveillerIsolement(Releves, Copie, Depot, Vus, Autre, Suivi, new VeilleServeur(veille), H, Reglages);
            if (passerLaGrace) H.Avancer(SurveillerIsolement.GraceDemarrage.TotalSeconds + 1);
        }

        /// <summary>Un passage après <paramref name="secondes"/> : les postes « en ligne » viennent d'être vus (healthcheck), puis la surveillance s'exécute.</summary>
        public async Task<IssueIsolement> Passage(double secondes)
        {
            H.Avancer(secondes);
            foreach (var p in EnLigne) Vus.Noter(p, H.MaintenantUtc);
            return await Surveiller.ExecuterAsync(Alertes);
        }

        public Banc Scenario(string[] connus, string[] enLigne)
        {
            Depot.Connus.AddRange(connus);
            EnLigne.AddRange(enLigne);
            foreach (var p in enLigne) Vus.Noter(p, H.MaintenantUtc);
            return this;
        }
    }

    private static readonly string[] Quatre = ["P1", "P2", "P3", "P4"];

    // ---- Isolement : décompte de 20 s ----

    [Fact]
    public async Task Copie_perdue_et_minorite_pendant_20_s_isole_le_serveur_sans_serveur_retenu_et_previent_le_titulaire()
    {
        var b = new Banc().Scenario(Quatre, ["P1"]);
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(0));
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(5));
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(5));
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(5));   // 20 s depuis le premier constat
        var e = b.Suivi.Ecart;
        Assert.NotNull(e);
        Assert.True(e!.Isole);
        Assert.True(e.EnMemoireSeulement);
        Assert.Null(e.ServeurRetenu);
        Assert.Single(b.Alertes.Messages);
    }

    [Fact]
    public async Task A_19_s_le_serveur_n_est_pas_isole()
    {
        var b = new Banc().Scenario(Quatre, ["P1"]);
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(0));
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(19));
        Assert.Null(b.Suivi.Ecart);
        Assert.Empty(b.Alertes.Messages);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(1));   // 20 s : la limite est atteinte
    }

    [Fact]
    public async Task Le_decompte_repart_de_zero_si_la_copie_revient_ou_la_majorite_est_revue_avant_20_s()
    {
        var b = new Banc().Scenario(Quatre, ["P1"]);
        await b.Passage(0);
        await b.Passage(15);
        b.Copie.Connectee = true;
        Assert.Equal(IssueIsolement.Normal, await b.Passage(3));
        b.Copie.Connectee = false;
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(1));   // nouveau décompte
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(19));
        Assert.Equal(IssueIsolement.Isole, await b.Passage(1));
    }

    [Fact]
    public async Task Egalite_le_serveur_1_garde_la_main()
    {
        var b = new Banc().Scenario(Quatre, ["P1", "P2"]);   // 2 vus sur 4
        for (var i = 0; i < 20; i++) Assert.Equal(IssueIsolement.Normal, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
        var impair = new Banc().Scenario(["P1", "P2"], ["P1"]);   // 1 sur 2 : égalité aussi
        for (var i = 0; i < 20; i++) Assert.Equal(IssueIsolement.Normal, await impair.Passage(5));
        Assert.Null(impair.Suivi.Ecart);
    }

    [Fact]
    public async Task Majorite_vue_ou_copie_connectee_le_serveur_garde_la_main()
    {
        var majorite = new Banc().Scenario(Quatre, ["P1", "P2", "P3"]);
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Normal, await majorite.Passage(5));
        var copie = new Banc().Scenario(Quatre, ["P1"]);
        copie.Copie.Connectee = true;
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Normal, await copie.Passage(5));
        Assert.Null(majorite.Suivi.Ecart);
        Assert.Null(copie.Suivi.Ecart);
    }

    [Fact]
    public async Task Un_poste_vu_mais_inconnu_de_la_base_ne_compte_pas()
    {
        var b = new Banc().Scenario(Quatre, ["P1", "X1", "X2", "X3"]);   // 4 « vus » mais 3 inconnus : un seul poste connu vu sur 4
        await b.Passage(0);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
    }

    [Fact]
    public async Task Un_poste_vu_depuis_plus_de_45_s_ne_compte_plus()
    {
        var b = new Banc().Scenario(Quatre, ["P1", "P2", "P3"]);
        Assert.Equal(IssueIsolement.Normal, await b.Passage(0));
        b.EnLigne.Clear();   // plus personne ne rappelle : les derniers appels datent d'il y a 45 s puis plus
        Assert.Equal(IssueIsolement.Normal, await b.Passage(44));
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(2));
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
    }

    // ---- Inactivité ----

    [Fact]
    public async Task Un_seul_serveur_declare_est_inactif()
    {
        var b = new Banc().Scenario(Quatre, []);
        b.Releves.Regle = false;
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Inactif, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Serveur_en_veille_est_inactif()
    {
        var b = new Banc(veille: true).Scenario(Quatre, []);
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Inactif, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Aucun_poste_connu_est_inactif_et_deux_minutes_de_grace_apres_le_demarrage()
    {
        var sansConnus = new Banc().Scenario([], []);
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Normal, await sansConnus.Passage(5));
        Assert.Null(sansConnus.Suivi.Ecart);

        var b = new Banc(passerLaGrace: false).Scenario(Quatre, []);
        for (var i = 0; i < 20; i++) Assert.Equal(IssueIsolement.Inactif, await b.Passage(5));   // 100 s
        Assert.Equal(IssueIsolement.Inactif, await b.Passage(19));   // 119 s
        Assert.Equal(IssueIsolement.EnAttente, await b.Passage(2));  // 121 s : la règle s'applique, le décompte démarre
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
    }

    [Fact]
    public async Task Autre_ecart_en_cours_ou_copie_illisible_aucune_decision()
    {
        var b = new Banc().Scenario(Quatre, []);
        b.Copie.Connectee = null;
        for (var i = 0; i < 10; i++) Assert.Equal(IssueIsolement.Inactif, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);

        var c = new Banc().Scenario(Quatre, []);
        c.Suivi.Restaurer(new EcartDetecte(c.H.MaintenantUtc, "AUTRE", 1, 2));
        Assert.Equal(IssueIsolement.Inactif, await c.Passage(30));
        Assert.False(c.Suivi.Ecart!.Isole);
    }

    // ---- Levée automatique ----

    private static async Task<Banc> Isole(string[]? enLigne = null)
    {
        var b = new Banc().Scenario(Quatre, enLigne ?? ["P1"]);
        await b.Passage(0);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
        return b;
    }

    [Fact]
    public async Task Copie_revenue_et_2e_serveur_en_veille_leve_l_isolement()
    {
        var b = await Isole();
        Assert.Equal(IssueIsolement.IsoleMaintenu, await b.Passage(5));
        b.Copie.Connectee = true;
        b.Autre.Etat = new EtatAutreServeurLu(true, "veille", true);
        Assert.Equal(IssueIsolement.Leve, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Majorite_revue_leve_l_isolement_meme_copie_perdue()
    {
        var b = await Isole();
        b.EnLigne.AddRange(["P2", "P3"]);   // 3 sur 4 reviennent
        Assert.Equal(IssueIsolement.Leve, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Moitie_revue_suffit_a_lever_egalite_le_serveur_1_garde_la_main()
    {
        var b = await Isole();
        b.EnLigne.Add("P2");   // 2 sur 4
        Assert.Equal(IssueIsolement.Leve, await b.Passage(5));
    }

    [Fact]
    public async Task Pas_de_levee_si_le_2e_serveur_est_devenu_principal_l_ecart_devient_definitif()
    {
        var b = await Isole();
        b.Copie.Connectee = true;
        b.Autre.Etat = new EtatAutreServeurLu(true, "principal", false);
        Assert.Equal(IssueIsolement.Definitif, await b.Passage(5));
        Assert.True(b.Suivi.Ecart!.Isole);
        // Définitif : même si, plus tard, il redevient « veille » dans ses réponses, on ne lève plus
        b.Autre.Etat = new EtatAutreServeurLu(true, "veille", true);
        Assert.Equal(IssueIsolement.Definitif, await b.Passage(5));
        Assert.NotNull(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Pas_de_levee_si_le_2e_serveur_repond_ecarte_ou_base_en_ecriture()
    {
        var b = await Isole();
        b.Copie.Connectee = true;
        b.Autre.Etat = new EtatAutreServeurLu(true, "ecarte", false);
        Assert.Equal(IssueIsolement.Definitif, await b.Passage(5));
        Assert.NotNull(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Autre_serveur_injoignable_leve_seulement_si_la_copie_est_revenue()
    {
        var b = await Isole();
        b.Autre.Etat = new EtatAutreServeurLu(false);
        b.EnLigne.AddRange(["P2", "P3"]);   // majorité revue mais le 2e serveur ne répond pas et la copie n'est pas revenue
        Assert.Equal(IssueIsolement.IsoleMaintenu, await b.Passage(5));
        Assert.NotNull(b.Suivi.Ecart);
        b.Copie.Connectee = true;
        Assert.Equal(IssueIsolement.Leve, await b.Passage(5));
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Les_postes_connus_sont_figes_a_l_isolement_la_table_qui_fond_ne_leve_rien()
    {
        var b = await Isole();
        b.EnLigne.Clear();
        b.Depot.Vide = true;   // plus d'écriture de présence : au bout de 3 minutes la base ne dirait plus aucun poste connu
        for (var i = 0; i < 60; i++) Assert.Equal(IssueIsolement.IsoleMaintenu, await b.Passage(10));   // 10 minutes
        Assert.NotNull(b.Suivi.Ecart);
    }

    [Fact]
    public async Task L_alerte_n_est_envoyee_qu_une_fois_par_heure_meme_si_l_isolement_revient()
    {
        var b = await Isole();
        b.Copie.Connectee = true;
        Assert.Equal(IssueIsolement.Leve, await b.Passage(5));
        b.Copie.Connectee = false;
        b.EnLigne.Clear();
        b.EnLigne.Add("P1");
        await b.Passage(5);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
        Assert.Single(b.Alertes.Messages);
        // Plus d'une heure plus tard : de nouveau permise
        b.Copie.Connectee = true;
        await b.Passage(5);
        b.Copie.Connectee = false;
        b.H.Avancer(3700);
        b.Vus.Noter("P1", b.H.MaintenantUtc);
        await b.Passage(0);
        Assert.Equal(IssueIsolement.Isole, await b.Passage(20));
        Assert.Equal(2, b.Alertes.Messages.Count);
    }

    // ---- Un isolement devient définitif : détection de timeline du lot 1 ----

    [Fact]
    public async Task Isole_puis_autre_serveur_principal_plus_recent_confirme_l_ecart_devient_definitif_motif_bascule()
    {
        var b = await Isole();
        var surveillance = new SurveillerServeurEcarte(b.Releves, b.Suivi, b.Alertes, b.H);
        b.Releves.Local = new ReleveServeur(false, 1, 7);
        // Autre serveur en recovery ou égal : l'isolement reste, aucune alerte de plus
        b.Releves.Autre = new ReleveServeur(true, null, null);
        await surveillance.ExecuterAsync();
        Assert.True(b.Suivi.Ecart!.Isole);
        // Autre serveur principal, timeline plus récente, deux relevés concordants
        b.Releves.Autre = new ReleveServeur(false, 2, 7);
        await surveillance.ExecuterAsync();
        Assert.True(b.Suivi.Ecart!.Isole);   // un seul relevé : jamais de décision
        await surveillance.ExecuterAsync();
        var e = b.Suivi.Ecart!;
        Assert.False(e.Isole);
        Assert.False(e.EnMemoireSeulement);   // gardé en base par le lot 3
        Assert.Equal("AUTRE", e.ServeurRetenu);
        Assert.Equal((1L, 2L), (e.TimelineLocale, e.TimelineRetenue));
    }

    // ---- Registre des postes vus ----

    [Fact]
    public void Le_registre_ecrit_au_plus_une_fois_par_minute_et_par_poste()
    {
        var r = new RegistrePostesVus();
        var t = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        Assert.True(r.Noter("P1", t));
        Assert.False(r.Noter("P1", t.AddSeconds(15)));
        Assert.False(r.Noter("P1", t.AddSeconds(59)));
        Assert.True(r.Noter("P1", t.AddSeconds(60)));
        Assert.True(r.Noter("P2", t.AddSeconds(61)));   // un autre poste a sa propre minute
        Assert.False(r.Noter("p2", t.AddSeconds(70)));  // casse ignorée
    }

    [Fact]
    public void Le_registre_borne_les_noms_et_le_nombre_de_postes()
    {
        Assert.Null(RegistrePostesVus.Normaliser(null));
        Assert.Null(RegistrePostesVus.Normaliser("   "));
        Assert.Null(RegistrePostesVus.Normaliser(new string('x', 101)));
        Assert.Null(RegistrePostesVus.Normaliser("P1\r\nX"));
        Assert.Equal("P1", RegistrePostesVus.Normaliser("  P1 "));
        Assert.NotNull(RegistrePostesVus.Normaliser(new string('x', 100)));

        var r = new RegistrePostesVus();
        var t = new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        for (var i = 0; i < RegistrePostesVus.PostesMax; i++) Assert.True(r.Noter("P" + i, t));
        Assert.False(r.Noter("EN-TROP", t));   // plus de place : ignoré
        Assert.True(r.Noter("EN-TROP", t.AddMinutes(11)));   // les postes de plus de 10 minutes sont oubliés
        Assert.Equal(["EN-TROP"], r.Vus(t.AddMinutes(11), TimeSpan.FromSeconds(45)).ToArray());
    }

    // ---- Présence en base : jamais bloquante, inactive en veille / écarté / un seul serveur ----

    private sealed class DepotPresence : IPresenceDepot
    {
        public List<string> Ecrits { get; } = [];
        public bool Echoue { get; set; }
        public Task NoterAsync(string poste, CancellationToken ct = default)
        {
            if (Echoue) throw new InvalidOperationException("base indisponible");
            Ecrits.Add(poste);
            return Task.CompletedTask;
        }
    }

    private sealed class Trace : ITraceErreurs
    {
        public int Nombre { get; private set; }
        public void Erreur(string message, Exception exception) => Nombre++;
    }

    private static PresencePostes Presence(Horloge h, DepotPresence depot, SuiviServeurEcarte suivi, bool veille = false, bool declare = true, Trace? trace = null, RegistrePostesVus? registre = null) =>
        new(registre ?? new RegistrePostesVus(), depot, suivi, new VeilleServeur(veille), new Releves { Regle = declare }, h, trace);

    [Fact]
    public async Task La_presence_s_ecrit_en_base_une_fois_par_minute_et_note_la_memoire_a_chaque_requete()
    {
        var h = new Horloge();
        var depot = new DepotPresence();
        var registre = new RegistrePostesVus();
        var p = Presence(h, depot, new SuiviServeurEcarte(), registre: registre);
        p.Noter("P1");
        await p.Derniere;
        h.Avancer(15);
        p.Noter("P1");
        await p.Derniere;
        Assert.Equal(["P1"], depot.Ecrits);
        Assert.Contains("P1", registre.Vus(h.MaintenantUtc, TimeSpan.FromSeconds(45)));
        h.Avancer(46);
        p.Noter("P1");
        await p.Derniere;
        Assert.Equal(["P1", "P1"], depot.Ecrits);
    }

    [Fact]
    public async Task La_presence_est_inactive_en_veille_sans_second_serveur_et_sans_en_tete()
    {
        var h = new Horloge();
        var depot = new DepotPresence();
        var registre = new RegistrePostesVus();
        Presence(h, depot, new SuiviServeurEcarte(), veille: true, registre: registre).Noter("P1");
        Presence(h, depot, new SuiviServeurEcarte(), declare: false, registre: registre).Noter("P2");
        var normal = Presence(h, depot, new SuiviServeurEcarte(), registre: registre);
        normal.Noter(null);
        normal.Noter("");
        await normal.Derniere;
        Assert.Empty(depot.Ecrits);
        Assert.Empty(registre.Vus(h.MaintenantUtc, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Serveur_ecarte_la_presence_reste_en_memoire_sans_ecrire_en_base()
    {
        var h = new Horloge();
        var depot = new DepotPresence();
        var suivi = new SuiviServeurEcarte();
        suivi.Restaurer(new EcartDetecte(h.MaintenantUtc, null, 0, 0, Isole: true));
        var registre = new RegistrePostesVus();
        var p = Presence(h, depot, suivi, registre: registre);
        p.Noter("P1");
        await p.Derniere;
        Assert.Empty(depot.Ecrits);
        Assert.Contains("P1", registre.Vus(h.MaintenantUtc, TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public async Task Une_erreur_d_ecriture_est_journalisee_sans_faire_echouer_la_requete()
    {
        var h = new Horloge();
        var trace = new Trace();
        var p = Presence(h, new DepotPresence { Echoue = true }, new SuiviServeurEcarte(), trace: trace);
        p.Noter("P1");   // ne lève rien
        await p.Derniere;
        Assert.Equal(1, trace.Nombre);
    }

    // ---- Preuve sur des scénarios : promotion (2e serveur) et maintien (serveur 1) ne sont jamais vrais ensemble ----

    [Fact]
    public async Task Promotion_cote_2e_serveur_et_maintien_cote_serveur_1_ne_sont_jamais_vrais_ensemble()
    {
        var scenarios = 0;
        var promotions = 0;
        var maintiens = 0;
        for (var c = 1; c <= 7; c++)
        {
            var connus = Enumerable.Range(1, c).Select(i => "P" + i).ToArray();
            for (var votants = 0; votants <= c; votants++)
                for (var vus = 0; vus <= c - votants; vus++)
                {
                    // Mêmes postes connus des deux côtés ; les votants (disent « serveur 1 perdu ») et les vus (appellent le serveur 1) sont disjoints,
                    // le reste (c - votants - vus) n'est ni l'un ni l'autre (éteint, ou sans rapport à temps)
                    var ceuxQuiVotent = connus.Take(votants).ToArray();
                    var ceuxQuiAppellent = connus.Skip(votants).Take(vus).ToArray();
                    var promotion = Promotion(connus, ceuxQuiVotent);
                    var maintien = await Maintien(connus, ceuxQuiAppellent);
                    Assert.False(promotion && maintien, $"connus={c} votants={votants} vus={vus} : promotion ET maintien");
                    scenarios++;
                    if (promotion) promotions++;
                    if (maintien) maintiens++;
                }
        }
        Assert.True(scenarios > 100 && promotions > 10 && maintiens > 10, "le jeu de scénarios doit couvrir les deux issues");
    }

    // Serveur 2 : serveur 1 muet (base et API) depuis 5 minutes, rapports récents des seuls votants
    private static bool Promotion(string[] connus, string[] votants)
    {
        var t = new DateTime(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
        var rapports = votants.Select(p => new RapportRetenu(p, false, t.AddSeconds(-5), t.AddSeconds(-300), false)).ToList();
        var entree = new EntreeDecision(t, true, connus.ToHashSet(StringComparer.OrdinalIgnoreCase), rapports,
            new Serveur1Vu(false, false, t.AddSeconds(-300), t.AddSeconds(-300)), t.AddHours(-1), new ReglagesBascule().Borner());
        return DecisionPromotion.Evaluer(entree).Verdict == VerdictPromotion.Promotion;
    }

    // Serveur 1 : copie perdue depuis plus de 20 s ; maintien = pas mis à l'écart
    private static async Task<bool> Maintien(string[] connus, string[] vus)
    {
        var b = new Banc().Scenario(connus, vus);
        for (var i = 0; i < 6; i++) await b.Passage(5);   // 25 s
        return b.Suivi.Ecart is null;
    }
}
