using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>
/// Lot V2-4 : exécutant de la promotion (2e serveur), retrait du serveur 1, alertes de la vigie, enregistrement des décisions au redémarrage.
/// Doublures seulement : aucune base, aucun réseau, aucune promotion réelle.
/// </summary>
public class PromotionAutomatiqueTests
{
    private static readonly DateTime T = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private const long Lignee = 7_000_000_000_000_000_001;

    // ---- Doublures ----
    private sealed class HorlogeSimulee(DateTime depart) : IHorloge
    {
        public DateTime Maintenant { get; set; } = depart;
        public DateTime MaintenantUtc => Maintenant;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(Maintenant);
    }

    private sealed class Releves(bool regle = true) : IReleveServeurs
    {
        public bool Joint { get; set; } = true;
        public bool AutreServeurRegle => regle;
        public string? AutreServeur => regle ? "serveur1" : null;
        public ReleveServeur? Local { get; set; } = new(true, null, null);
        public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) => Task.FromResult(Local);
        public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) => Task.FromResult(Joint ? new ReleveServeur(false, 1, Lignee) : null);
    }

    private sealed class EtatApi(bool joint) : ILecteurEtatServeur1
    {
        public bool Joint { get; set; } = joint;
        public Task<EtatServeur1?> EtatServeur1Async(CancellationToken ct = default) =>
            Task.FromResult(Joint ? new EtatServeur1("s1", "principal", 1, false, "1", null) : null);
    }

    private sealed class PostesFiges(DateTime reference, params PosteVuEnBase[] postes) : IPostesConnusDepot
    {
        public Task<PostesConnusLus> LireAsync(CancellationToken ct = default) => Task.FromResult(new PostesConnusLus(reference, postes));
    }

    private sealed class Journal : IJournalDecisions
    {
        public List<string> Evenements { get; } = [];
        public Task EcrireAsync(string evenement, object donnees, CancellationToken ct = default) { Evenements.Add(evenement); return Task.CompletedTask; }
    }

    private sealed class Base : IBasePromotion
    {
        public EtatCopie? Copie { get; set; } = new(true, Lignee, 1, T.AddSeconds(-3));
        public bool LigneRetrait { get; set; } = true;
        public bool LsnAtteint { get; set; } = true;
        public ResultatPromotionBase Resultat { get; set; } = ResultatPromotionBase.Promue;
        public int Promotions { get; private set; }
        public string? LsnDemande { get; private set; }
        public Task<EtatCopie?> LireCopieAsync(CancellationToken ct = default) => Task.FromResult(Copie);
        public Task<bool> RetraitVisibleAsync(CancellationToken ct = default) => Task.FromResult(LigneRetrait);
        public Task<bool> LsnRejoueAtteintAsync(string lsn, CancellationToken ct = default) { LsnDemande = lsn; return Task.FromResult(LsnAtteint); }
        public Task<ResultatPromotionBase> PromouvoirAsync(CancellationToken ct = default) { Promotions++; return Task.FromResult(Resultat); }
    }

    private sealed class Retrait : IClientRetraitServeur1
    {
        public ReponseRetrait Reponse { get; set; } = new(true, 200, "0/16B3748");
        public int Appels { get; private set; }
        public Task<ReponseRetrait> DemanderRetraitAsync(CancellationToken ct = default) { Appels++; return Task.FromResult(Reponse); }
    }

    private sealed class AlertesServeur1 : IAlertesVersServeur1
    {
        public bool Joignable { get; set; } = true;
        public List<string> Types { get; } = [];
        public Task<bool> EnvoyerAsync(string type, string? poste, string message, DateTime survenueUtc, CancellationToken ct = default)
        {
            Types.Add(type);
            return Task.FromResult(Joignable);
        }
    }

    private sealed class Fichiers : IFichiersDecisionBascule
    {
        public List<DecisionBascule> Ecrits { get; } = [];
        public List<string> Annules { get; } = [];
        public List<string> Enregistres { get; } = [];
        public List<FichierDecision> Attendus { get; } = [];
        public Task<string> EcrireAsync(DecisionBascule decision, CancellationToken ct = default) { Ecrits.Add(decision); return Task.FromResult($"bascule-{Ecrits.Count}.json"); }
        public Task<IReadOnlyList<FichierDecision>> NonEnregistresAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<FichierDecision>>(Attendus.ToList());
        public Task MarquerEnregistreAsync(string chemin, CancellationToken ct = default) { Enregistres.Add(chemin); Attendus.RemoveAll(f => f.Chemin == chemin); return Task.CompletedTask; }
        public Task AnnulerAsync(string chemin, CancellationToken ct = default) { Annules.Add(chemin); return Task.CompletedTask; }
    }

    private sealed class Banc
    {
        public HorlogeSimulee H { get; } = new(T);
        public Releves Releves { get; }
        public EtatApi Api { get; } = new(true);
        public Journal Journal { get; } = new();
        public Base Base { get; } = new();
        public Retrait Retrait { get; } = new();
        public AlertesServeur1 Alertes { get; } = new();
        public Fichiers Fichiers { get; } = new();
        public VeilleServeur Veille { get; } = new(true);
        public ReglagesBascule Reglages { get; } = new();
        public RegistreRapportsPostes Registre { get; }
        public VigieSecondServeur Vigie { get; }
        public ExecutantPromotion Executant { get; }
        public bool ContexteInterdit { get; set; }

        public Banc(bool declare = true)
        {
            Releves = new Releves(declare);
            Registre = new RegistreRapportsPostes(Reglages);
            var postes = Enumerable.Range(1, 4).Select(i => new PosteVuEnBase($"P{i}", T.AddSeconds(-20))).ToArray();
            var sonde = new SondeServeur1(Releves, Api);   // la même sonde pour la vigie et l'exécutant (lignée du serveur 1 relevée par la vigie)
            Vigie = new VigieSecondServeur(Registre, new PostesFiges(T, postes), sonde, Releves, Journal, H, Reglages);
            Executant = new ExecutantPromotion(Vigie, Registre, sonde, Releves, Base, Retrait, Alertes, new LimiteAlertesHoraire(H), Fichiers, Journal, Veille, H,
                Reglages, () => ContexteInterdit, (_, _) => Task.CompletedTask);
            H.Maintenant = T.AddSeconds(200);   // le démarrage de la veille est passé
        }

        public async Task<ResultatDecision> TourAsync(bool authentifies, params string[] perdants)
        {
            H.Maintenant = H.Maintenant.AddSeconds(5);
            foreach (var p in perdants) await Vigie.TraiterRapportAsync(new RapportPoste(p, false, null, "1", "x"), "adr-" + p, authentifies);
            return await Vigie.EvaluerAsync();
        }
    }

    /// <summary>Cas 1 : le serveur 1 est vu joint (lignée notée), puis muet ; 3 postes sur 4 le perdent pendant plus de 60 s.</summary>
    private static async Task<ResultatDecision> VerdictCas1Async(Banc b)
    {
        await b.Vigie.EvaluerAsync();
        b.Releves.Joint = false;
        b.Api.Joint = false;
        ResultatDecision r = null!;
        for (var i = 0; i < 20; i++) r = await b.TourAsync(false, "P1", "P2", "P3");
        return r;
    }

    /// <summary>Cas 2 : le serveur 1 reste joint par le 2e serveur, 3 postes sur 4 (authentifiés) ne le joignent plus depuis plus de 60 s.</summary>
    private static async Task<ResultatDecision> VerdictCas2Async(Banc b)
    {
        await b.Vigie.EvaluerAsync();
        ResultatDecision r = null!;
        for (var i = 0; i < 16; i++) r = await b.TourAsync(true, "P1", "P2", "P3");
        return r;
    }

    // ---- Exécutant : cas 1 ----
    [Fact]
    public async Task Cas1_promotion_confirmee_ecrit_le_fichier_de_decision_puis_promeut_et_demande_le_redemarrage()
    {
        var b = new Banc();
        var r = await VerdictCas1Async(b);
        Assert.Equal(VerdictPromotion.Promotion, r.Verdict);
        Assert.True(b.Vigie.Confirmations >= 2);

        var res = await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal(IssuePromotion.Promue, res.Issue);
        Assert.Equal(1, b.Base.Promotions);
        Assert.Equal(0, b.Retrait.Appels);   // cas 1 : le serveur 1 est injoignable, aucun retrait
        var d = Assert.Single(b.Fichiers.Ecrits);
        Assert.Equal(CasBascule.Promotion, d.Cas);
        Assert.Null(d.LsnFinal);
        Assert.Equal(1, d.TimelineAvant);
        Assert.Equal(3, d.Votes);
        Assert.Equal(4, d.Connus);
        Assert.All(d.Rapports, x => Assert.StartsWith("P", x.Poste));   // noms de postes seulement
        Assert.NotNull(d.DerniereTransactionRecueUtc);
        Assert.True(b.Veille.RedemarrageDemande);
        Assert.Equal("promotion", b.Veille.MotifRedemarrage);
        Assert.True(b.Executant.Promue);
    }

    [Fact]
    public async Task Verdict_non_confirme_deux_fois_aucune_promotion()
    {
        var b = new Banc();
        await b.Vigie.EvaluerAsync();
        b.Releves.Joint = false;
        b.Api.Joint = false;
        ResultatDecision r = null!;
        // Dès que le verdict devient « Promotion » pour la première fois : une seule confirmation
        for (var i = 0; i < 40 && r?.Verdict != VerdictPromotion.Promotion; i++) r = await b.TourAsync(false, "P1", "P2", "P3");
        Assert.Equal(1, b.Vigie.Confirmations);
        Assert.Equal(IssuePromotion.Aucune, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        Assert.Equal(0, b.Base.Promotions);
    }

    [Fact]
    public async Task Une_seule_promotion_par_processus_meme_si_le_verdict_reste_confirme()
    {
        var b = new Banc();
        var r = await VerdictCas1Async(b);
        Assert.Equal(IssuePromotion.Promue, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        for (var i = 0; i < 3; i++)
        {
            r = await b.TourAsync(false, "P1", "P2", "P3");
            Assert.Equal(IssuePromotion.Aucune, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        }
        Assert.Equal(1, b.Base.Promotions);
        Assert.Single(b.Fichiers.Ecrits);
    }

    [Fact]
    public async Task Echec_apres_tentative_pas_de_deuxieme_essai_dans_le_meme_processus()
    {
        var b = new Banc();
        b.Base.Resultat = ResultatPromotionBase.NonPromue;
        var r = await VerdictCas1Async(b);
        Assert.Equal(IssuePromotion.NonPromue, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        r = await b.TourAsync(false, "P1", "P2", "P3");
        Assert.Equal(IssuePromotion.Aucune, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        Assert.Equal(1, b.Base.Promotions);
        Assert.False(b.Veille.RedemarrageDemande);
    }

    // ---- Exécutant : garde-fous ----
    [Fact]
    public async Task Lignee_illisible_faute_du_droit_163b_pas_de_promotion_journal_et_alerte()
    {
        var b = new Banc();
        b.Base.Copie = new EtatCopie(true, null, null, T);
        var r = await VerdictCas1Async(b);

        var res = await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal(IssuePromotion.NonPromue, res.Issue);
        Assert.Equal("lignee_illisible", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Contains("promotion_refusee", b.Journal.Evenements);
        Assert.Contains(TypesAlerteInterServeurs.PromotionManuelleNecessaire, b.Alertes.Types);
        Assert.Empty(b.Fichiers.Ecrits);
    }

    [Fact]
    public async Task Lignee_differente_du_serveur_1_pas_de_promotion()
    {
        var b = new Banc();
        b.Base.Copie = new EtatCopie(true, Lignee + 1, 1, T);
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas1Async(b));
        Assert.Equal("lignee_differente", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
    }

    [Fact]
    public async Task Lignee_du_serveur_1_jamais_lue_pas_de_promotion()
    {
        var b = new Banc();
        b.Releves.Joint = false;   // jamais joint depuis le démarrage de la veille : aucune lignée connue
        b.Api.Joint = false;
        ResultatDecision r = null!;
        for (var i = 0; i < 20; i++) r = await b.TourAsync(false, "P1", "P2", "P3");
        Assert.Equal(VerdictPromotion.Promotion, r.Verdict);
        Assert.Equal("lignee_serveur1_inconnue", (await b.Executant.ApresEvaluationAsync(r)).Raison);
        Assert.Equal(0, b.Base.Promotions);
    }

    [Fact]
    public async Task Promotion_automatique_desactivee_pas_de_promotion_alerte_promotion_manuelle_necessaire()
    {
        var b = new Banc();
        b.Reglages.PromotionAutomatique = false;
        var r = await VerdictCas1Async(b);

        var res = await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal("promotion_automatique_desactivee", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Equal([TypesAlerteInterServeurs.PromotionManuelleNecessaire], b.Alertes.Types);
        Assert.False(b.Veille.RedemarrageDemande);
    }

    [Fact]
    public async Task Alerte_promotion_manuelle_serveur1_injoignable_journal_seulement_sans_erreur()
    {
        var b = new Banc();
        b.Reglages.PromotionAutomatique = false;
        b.Alertes.Joignable = false;
        await b.Executant.ApresEvaluationAsync(await VerdictCas1Async(b));
        Assert.Contains("alerte_non_transmise", b.Journal.Evenements);
        Assert.DoesNotContain("alerte_transmise", b.Journal.Evenements);
    }

    [Fact]
    public async Task Droit_pg_promote_absent_pas_de_promotion_fichier_annule_et_alerte()
    {
        var b = new Banc();
        b.Base.Resultat = ResultatPromotionBase.DroitAbsent;
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas1Async(b));

        Assert.Equal("droit_pg_promote_absent", res.Raison);
        Assert.Equal(1, b.Base.Promotions);   // l'appel a été tenté, PostgreSQL l'a refusé (42501)
        Assert.Single(b.Fichiers.Annules);    // le fichier de décision ne doit pas être repris comme une promotion faite
        Assert.Contains(TypesAlerteInterServeurs.PromotionManuelleNecessaire, b.Alertes.Types);
        Assert.False(b.Veille.RedemarrageDemande);
        Assert.False(b.Executant.Promue);
    }

    [Fact]
    public async Task Base_deja_en_ecriture_ou_demo_ou_restauration_aucune_promotion()
    {
        var b = new Banc();
        b.Base.Copie = new EtatCopie(false, Lignee, 2, T);
        Assert.Equal(IssuePromotion.Aucune, (await b.Executant.ApresEvaluationAsync(await VerdictCas1Async(b))).Issue);
        var c = new Banc { ContexteInterdit = true };
        Assert.Equal("demo_ou_restauration", (await c.Executant.ApresEvaluationAsync(await VerdictCas1Async(c))).Raison);
        Assert.Equal(0, b.Base.Promotions + c.Base.Promotions);
    }

    [Fact]
    public async Task Officine_a_un_seul_serveur_tout_est_inactif()
    {
        var b = new Banc(declare: false);
        for (var i = 0; i < 20; i++) await b.TourAsync(false, "P1", "P2", "P3");
        Assert.Equal(VerdictPromotion.Inactif, b.Vigie.Dernier!.Verdict);

        var res = await b.Executant.ApresEvaluationAsync(b.Vigie.Dernier);

        Assert.Equal(IssuePromotion.Inactive, res.Issue);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Equal(0, b.Retrait.Appels);
        Assert.Empty(b.Alertes.Types);
        Assert.Empty(b.Fichiers.Ecrits);
        Assert.False(b.Veille.RedemarrageDemande);
    }

    // ---- Exécutant : cas 2 et ses trois preuves ----
    [Fact]
    public async Task Cas2_les_trois_preuves_reunies_promeut_et_inscrit_le_lsn_final()
    {
        var b = new Banc();
        var r = await VerdictCas2Async(b);
        Assert.Equal(VerdictPromotion.RetraitPuisPromotion, r.Verdict);

        var res = await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal(IssuePromotion.Promue, res.Issue);
        Assert.Equal(1, b.Retrait.Appels);
        Assert.Equal("0/16B3748", b.Base.LsnDemande);
        Assert.Equal(1, b.Base.Promotions);
        var d = Assert.Single(b.Fichiers.Ecrits);
        Assert.Equal(CasBascule.RetraitPromotion, d.Cas);
        Assert.Equal("0/16B3748", d.LsnFinal);
        Assert.True(b.Veille.RedemarrageDemande);
    }

    [Theory]
    [InlineData("non_fiable")]
    [InlineData("statut_409")]
    [InlineData("statut_503")]
    [InlineData("sans_lsn")]
    public async Task Cas2_preuve_1_reponse_signee_200_avec_lsn_manquante_pas_de_promotion(string variante)
    {
        var b = new Banc();
        b.Retrait.Reponse = variante switch
        {
            "non_fiable" => new ReponseRetrait(false, 200, "0/16B3748"),
            "statut_409" => new ReponseRetrait(true, 409, null),
            "statut_503" => new ReponseRetrait(true, 503, null),
            _ => new ReponseRetrait(true, 200, null)
        };
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas2Async(b));

        Assert.Equal(IssuePromotion.NonPromue, res.Issue);
        Assert.Equal("retrait_sans_reponse_fiable", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Empty(b.Fichiers.Ecrits);
        Assert.Contains(TypesAlerteInterServeurs.PromotionManuelleNecessaire, b.Alertes.Types);
    }

    [Fact]
    public async Task Cas2_preuve_2_ligne_retrait_absente_de_la_copie_pas_de_promotion()
    {
        var b = new Banc();
        b.Base.LigneRetrait = false;
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas2Async(b));
        Assert.Equal("ligne_retrait_absente_de_la_copie", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Empty(b.Fichiers.Ecrits);
        Assert.Contains(TypesAlerteInterServeurs.PromotionManuelleNecessaire, b.Alertes.Types);
    }

    [Fact]
    public async Task Cas2_preuve_3_lsn_final_non_rejoue_pas_de_promotion()
    {
        var b = new Banc();
        b.Base.LsnAtteint = false;
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas2Async(b));
        Assert.Equal("lsn_final_non_rejoue", res.Raison);
        Assert.Equal(0, b.Base.Promotions);
        Assert.Empty(b.Fichiers.Ecrits);
        Assert.False(b.Veille.RedemarrageDemande);
    }

    // Le retrait est rejouable : une réponse 200 perdue ou une copie en retard ne doivent pas laisser l'officine sans serveur en écriture
    [Fact]
    public async Task Cas2_serveur1_deja_ecarte_par_retrait_409_avec_lsn_et_preuves_promotion()
    {
        var b = new Banc();
        b.Retrait.Reponse = new ReponseRetrait(true, 409, "0/16B3800");
        var res = await b.Executant.ApresEvaluationAsync(await VerdictCas2Async(b));

        Assert.Equal(IssuePromotion.Promue, res.Issue);
        Assert.Equal("0/16B3800", b.Base.LsnDemande);
        Assert.Equal(1, b.Base.Promotions);
    }

    [Fact]
    public async Task Cas2_reponse_du_retrait_perdue_puis_409_au_tour_suivant_promotion()
    {
        var b = new Banc();
        b.Retrait.Reponse = new ReponseRetrait(false, null, null);   // réponse perdue : le serveur 1 s'est peut-être mis hors jeu
        var r = await VerdictCas2Async(b);
        Assert.Equal(IssuePromotion.NonPromue, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        Assert.Equal(0, b.Base.Promotions);

        b.Retrait.Reponse = new ReponseRetrait(true, 409, "0/16B3800");   // nouvel appel : déjà écarté par un retrait
        r = await b.TourAsync(true, "P1", "P2", "P3");
        var res = await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal(IssuePromotion.Promue, res.Issue);
        Assert.Equal(2, b.Retrait.Appels);
        Assert.Equal(1, b.Base.Promotions);
    }

    [Fact]
    public async Task Cas2_ligne_de_retrait_pas_encore_dans_la_copie_puis_arrivee_promotion_au_tour_suivant()
    {
        var b = new Banc();
        b.Base.LigneRetrait = false;
        var r = await VerdictCas2Async(b);
        Assert.Equal("ligne_retrait_absente_de_la_copie", (await b.Executant.ApresEvaluationAsync(r)).Raison);

        b.Base.LigneRetrait = true;   // la réplication a rattrapé son retard
        r = await b.TourAsync(true, "P1", "P2", "P3");
        Assert.Equal(IssuePromotion.Promue, (await b.Executant.ApresEvaluationAsync(r)).Issue);
        Assert.Equal(1, b.Base.Promotions);
    }

    // ---- Alertes de la vigie ----
    [Fact]
    public async Task Alerte_api_serveur1_arretee_une_par_heure_au_plus_puis_de_nouveau_apres_une_heure()
    {
        var b = new Banc();
        await b.Vigie.EvaluerAsync();
        b.Api.Joint = false;   // base vivante, API muette
        ResultatDecision r = null!;
        for (var i = 0; i < 10; i++)
        {
            r = await b.TourAsync(false, "P1");
            await b.Executant.ApresEvaluationAsync(r);
        }
        Assert.Equal(VerdictPromotion.ApiServeur1Muette, r.Verdict);
        Assert.Equal([ConstantesVigie.AlerteApiServeur1Arretee], b.Alertes.Types);   // une seule, malgré les tours suivants
        Assert.Equal(0, b.Base.Promotions);

        b.H.Maintenant = b.H.Maintenant.AddMinutes(61);
        await b.Executant.ApresEvaluationAsync(r);
        Assert.Equal(2, b.Alertes.Types.Count);
    }

    [Fact]
    public async Task Alerte_egalite_majorite_serveur1_injoignable_journal_seulement()
    {
        var b = new Banc();
        await b.Vigie.EvaluerAsync();
        b.Releves.Joint = false;
        b.Api.Joint = false;
        b.Alertes.Joignable = false;   // l'égalité se produit quand le serveur 1 est injoignable : l'alerte ne peut pas partir
        ResultatDecision r = null!;
        for (var i = 0; i < 20; i++) r = await b.TourAsync(false, "P1", "P2");
        Assert.Equal(VerdictPromotion.Egalite, r.Verdict);

        await b.Executant.ApresEvaluationAsync(r);
        await b.Executant.ApresEvaluationAsync(r);

        Assert.Equal([ConstantesVigie.AlerteEgaliteMajorite], b.Alertes.Types);   // une tentative par heure
        Assert.Contains("alerte_non_transmise", b.Journal.Evenements);
        Assert.Equal(0, b.Base.Promotions);
    }

    // ---- Route retrait (serveur 1) ----
    private sealed class Ecritures(List<string> ordre) : IEcrituresServeur
    {
        public bool Terminees { get; set; } = true;
        public Task<bool> AttendreFinTransactionsApiAsync(TimeSpan delai, CancellationToken ct = default) { ordre.Add("attente:" + delai.TotalSeconds); return Task.FromResult(Terminees); }
        public Task<string?> LsnCourantAsync(CancellationToken ct = default) { ordre.Add("lsn"); return Task.FromResult<string?>("0/16B3748"); }
    }

    private sealed class DepotEcarts(List<string> ordre) : IEcartsDepot
    {
        public EcartDetecte? Actif { get; set; }
        public List<EcartDetecte> Ecrits { get; } = [];
        public Task<EcartDetecte?> LireActifAsync(CancellationToken ct = default) => Task.FromResult(Actif);
        public Task<bool> EnregistrerAsync(EcartDetecte ecart, string motif, CancellationToken ct = default)
        {
            ordre.Add("ligne");
            Ecrits.Add(ecart);
            Actif = ecart;
            return Task.FromResult(true);
        }
        public Task<bool> LeverAsync(string par, string motif, CancellationToken ct = default) { Actif = null; return Task.FromResult(true); }
    }

    private sealed class AuditMemoire : IJournalSysteme
    {
        public List<string> Actions { get; } = [];
        public Task EcrireAsync(string action, string? tableCible, IReadOnlyDictionary<string, object?> details, CancellationToken ct = default) { Actions.Add(action); return Task.CompletedTask; }
    }

    private sealed class Verrou : IVerrouBase
    {
        public List<bool> Appels { get; } = [];
        public void Appliquer(bool lectureSeule) => Appels.Add(lectureSeule);
    }

    private sealed class BancRetrait
    {
        public List<string> Ordre { get; } = [];
        public SuiviServeurEcarte Suivi { get; } = new();
        public Ecritures Ecritures { get; }
        public DepotEcarts Depot { get; }
        public AuditMemoire Audit { get; } = new();
        public Verrou Verrou { get; } = new();
        public GardienServeurEcarte Gardien { get; }
        public RetraitServeur Retrait { get; }

        public BancRetrait(bool veille = false, bool deuxServeurs = true)
        {
            Ecritures = new Ecritures(Ordre);
            Depot = new DepotEcarts(Ordre);
            var releves = new Releves(deuxServeurs) { Local = new ReleveServeur(false, 1, Lignee) };
            Gardien = new GardienServeurEcarte(Suivi, Verrou, Depot, releves);
            Gardien.Demarrer();
            Retrait = new RetraitServeur(releves, Suivi, Depot, Ecritures, new VeilleServeur(veille), new HorlogeSimulee(T), Audit);
        }
    }

    [Fact]
    public async Task Retrait_nominal_pose_l_ecart_attend_les_transactions_ecrit_la_ligne_puis_lit_le_lsn_apres()
    {
        var b = new BancRetrait();

        var r = await b.Retrait.ExecuterAsync();
        await b.Gardien.Derniere;

        Assert.Equal(IssueRetrait.Ecarte, r.Issue);
        Assert.Equal("0/16B3748", r.LsnFinal);
        Assert.Equal(["attente:10", "ligne", "lsn"], b.Ordre);   // le LSN est lu APRÈS l'écriture de la ligne, qui suit la fin des transactions
        var ecart = Assert.Single(b.Depot.Ecrits);              // une seule écriture : le gardien n'écrit pas l'écart d'un retrait
        Assert.True(ecart.Retrait);
        Assert.Equal("serveur1", ecart.ServeurRetenu);
        Assert.Equal(0, ecart.TimelineRetenue);
        Assert.NotNull(b.Suivi.Ecart);
        Assert.Equal([true], b.Verrou.Appels);                  // verrou de la base posé (connexions en lecture seule)
        Assert.Equal([RetraitServeur.ActionAudit], b.Audit.Actions);
    }

    [Fact]
    public async Task Retrait_deja_ecarte_409_et_deuxieme_appel_aussi()
    {
        var b = new BancRetrait();
        Assert.Equal(IssueRetrait.Ecarte, (await b.Retrait.ExecuterAsync()).Issue);
        var deux = await b.Retrait.ExecuterAsync();
        Assert.Equal(IssueRetrait.DejaEcarte, deux.Issue);
        Assert.Equal("0/16B3748", deux.LsnFinal);   // écart de retrait : LSN connu, donné pour le contrat

        var c = new BancRetrait();
        c.Suivi.Restaurer(new EcartDetecte(T, "serveur1", 1, 2));   // écart de nature « bascule » déjà posé
        var r = await c.Retrait.ExecuterAsync();
        Assert.Equal(IssueRetrait.DejaEcarte, r.Issue);
        Assert.Null(r.LsnFinal);
    }

    [Fact]
    public async Task Retrait_ecart_actif_en_base_seulement_409()
    {
        var b = new BancRetrait();
        b.Depot.Actif = new EcartDetecte(T, "serveur1", 1, 2);
        Assert.Equal(IssueRetrait.DejaEcarte, (await b.Retrait.ExecuterAsync()).Issue);
        Assert.Null(b.Suivi.Ecart);
    }

    [Fact]
    public async Task Retrait_sur_une_api_en_veille_ou_sans_second_serveur_503_rien_n_est_pose()
    {
        var veille = new BancRetrait(veille: true);
        Assert.Equal(IssueRetrait.Impossible, (await veille.Retrait.ExecuterAsync()).Issue);
        Assert.Null(veille.Suivi.Ecart);
        Assert.Empty(veille.Verrou.Appels);

        var seul = new BancRetrait(deuxServeurs: false);
        Assert.Equal(IssueRetrait.Impossible, (await seul.Retrait.ExecuterAsync()).Issue);
        Assert.Null(seul.Suivi.Ecart);
    }

    [Fact]
    public async Task Retrait_transactions_non_terminees_dans_le_delai_503_et_l_etat_pose_est_leve()
    {
        var b = new BancRetrait();
        b.Ecritures.Terminees = false;

        var r = await b.Retrait.ExecuterAsync();
        await b.Gardien.Derniere;

        Assert.Equal(IssueRetrait.Impossible, r.Issue);
        Assert.Null(b.Suivi.Ecart);                       // rien d'écrit en base : le serveur reprend l'écriture
        Assert.Empty(b.Depot.Ecrits);
        Assert.Equal([true, false], b.Verrou.Appels);
        Assert.DoesNotContain("lsn", b.Ordre);
    }

    // ---- Alertes reçues du 2e serveur (serveur 1) ----
    private sealed class AlertesTitulaire : IAlertesTitulaire
    {
        public List<string> Objets { get; } = [];
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task SecondServeurAsync(string objet, string detail, string sms, CancellationToken ct = default) { Objets.Add(objet + "|" + detail); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Alerte_recue_au_plus_une_par_heure_et_par_type_et_par_poste_type_inconnu_refuse()
    {
        var h = new HorlogeSimulee(T);
        var titulaire = new AlertesTitulaire();
        var recevoir = new RecevoirAlerteInterServeurs(titulaire, new LimiteAlertesHoraire(h));

        Assert.Equal(IssueAlerteRecue.Acceptee, await recevoir.ExecuterAsync("api_serveur1_arretee", null, "API arrêtée"));
        Assert.Equal(IssueAlerteRecue.Limitee, await recevoir.ExecuterAsync("api_serveur1_arretee", null, "API arrêtée"));
        Assert.Equal(IssueAlerteRecue.Acceptee, await recevoir.ExecuterAsync("egalite_majorite", null, "égalité"));
        Assert.Equal(IssueAlerteRecue.Acceptee, await recevoir.ExecuterAsync("reseau_poste", "P1", "réseau"));
        Assert.Equal(IssueAlerteRecue.Acceptee, await recevoir.ExecuterAsync("reseau_poste", "P2", "réseau"));
        Assert.Equal(IssueAlerteRecue.Limitee, await recevoir.ExecuterAsync("reseau_poste", "P1", "réseau"));
        Assert.Equal(4, titulaire.Objets.Count);

        h.Maintenant = h.Maintenant.AddMinutes(61);
        Assert.Equal(IssueAlerteRecue.Acceptee, await recevoir.ExecuterAsync("api_serveur1_arretee", null, "API arrêtée"));

        Assert.Equal(IssueAlerteRecue.TypeInconnu, await recevoir.ExecuterAsync("autre", null, "x"));
        Assert.Equal(IssueAlerteRecue.PosteManquant, await recevoir.ExecuterAsync("reseau_poste", null, "x"));
        Assert.Equal(IssueAlerteRecue.MessageInvalide, await recevoir.ExecuterAsync("egalite_majorite", null, new string('x', 501)));
    }

    // ---- Enregistrement des décisions au redémarrage en principal ----
    private sealed class DepotDecisions : IDecisionsBasculeDepot
    {
        public List<(DecisionBascule Decision, int? TimelineApres)> Lignes { get; } = [];
        public Task<bool> AjouterAsync(DecisionBascule decision, int? timelineApres, CancellationToken ct = default)
        {
            if (Lignes.Any(l => l.Decision.DecideLeUtc == decision.DecideLeUtc && l.Decision.Cas == decision.Cas)) return Task.FromResult(false);
            Lignes.Add((decision, timelineApres));
            return Task.FromResult(true);
        }
    }

    private static DecisionBascule Decision(string cas) =>
        new(cas, "Promotion", T, 4, 3, 3, 0, [new RapportBascule("P1", false, T, T.AddSeconds(-90), true)], new SondesBascule(false, false, T, T),
            cas == CasBascule.RetraitPromotion ? "0/16B3748" : null, T.AddSeconds(-4), 1, "cas_1_injoignable_majorite");

    [Theory]
    [InlineData(CasBascule.Promotion, true)]
    [InlineData(CasBascule.RetraitPromotion, false)]
    public async Task Redemarrage_en_principal_enregistre_la_decision_une_fois_alerte_et_renomme_le_fichier(string cas, bool mentionneLaReprise)
    {
        var fichiers = new Fichiers();
        fichiers.Attendus.Add(new FichierDecision("bascule-1.json", Decision(cas)));
        var depot = new DepotDecisions();
        var audit = new AuditMemoire();
        var titulaire = new AlertesTitulaire();
        var releves = new Releves { Local = new ReleveServeur(false, 2, Lignee) };
        var enregistrer = new EnregistrerDecisionsBascule(fichiers, depot, releves, audit, titulaire);

        Assert.Equal(1, await enregistrer.ExecuterAsync());
        Assert.Equal(0, await enregistrer.ExecuterAsync());   // idempotent : le fichier est renommé, rien de plus

        var ligne = Assert.Single(depot.Lignes);
        Assert.Equal(2, ligne.TimelineApres);   // timeline courante
        Assert.Equal(1, ligne.Decision.TimelineAvant);
        Assert.Equal([EnregistrerDecisionsBascule.ActionAudit], audit.Actions);
        var alerte = Assert.Single(titulaire.Objets);
        Assert.Contains("Dernière transaction reçue", alerte);
        Assert.Equal(mentionneLaReprise, alerte.Contains("à reprendre"));
        Assert.Equal(["bascule-1.json"], fichiers.Enregistres);
    }

    [Fact]
    public async Task Redemarrage_arret_entre_l_ecriture_en_base_et_le_renommage_ne_double_ni_la_ligne_ni_l_alerte()
    {
        var fichiers = new Fichiers();
        fichiers.Attendus.Add(new FichierDecision("bascule-1.json", Decision(CasBascule.Promotion)));
        var depot = new DepotDecisions();
        depot.Lignes.Add((Decision(CasBascule.Promotion), 2));   // déjà en base (le renommage n'a pas eu lieu)
        var audit = new AuditMemoire();
        var titulaire = new AlertesTitulaire();
        var enregistrer = new EnregistrerDecisionsBascule(fichiers, depot, new Releves { Local = new ReleveServeur(false, 2, Lignee) }, audit, titulaire);

        Assert.Equal(0, await enregistrer.ExecuterAsync());

        Assert.Single(depot.Lignes);
        Assert.Empty(audit.Actions);
        Assert.Empty(titulaire.Objets);
        Assert.Equal(["bascule-1.json"], fichiers.Enregistres);   // mais le fichier est bien marqué
    }

    [Fact]
    public async Task Fichier_de_decision_illisible_laisse_en_place_sans_ligne()
    {
        var fichiers = new Fichiers();
        fichiers.Attendus.Add(new FichierDecision("bascule-1.json", null));
        var depot = new DepotDecisions();
        var enregistrer = new EnregistrerDecisionsBascule(fichiers, depot, new Releves(), new AuditMemoire(), new AlertesTitulaire());
        Assert.Equal(0, await enregistrer.ExecuterAsync());
        Assert.Empty(depot.Lignes);
        Assert.Empty(fichiers.Enregistres);
    }
}
