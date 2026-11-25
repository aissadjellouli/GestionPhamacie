using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Lot V2-3 : décision de promotion (fonction pure, horloge simulée), postes connus, registre des rapports, vigie.</summary>
public class DecisionPromotionTests
{
    private static readonly DateTime T = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Quatre = ["P1", "P2", "P3", "P4"];

    private static EntreeDecision Entree(IEnumerable<string>? connus = null, IEnumerable<RapportRetenu>? rapports = null, Serveur1Vu? s1 = null,
        bool declare = true, int demarreIlYaSecondes = 3600, ReglagesBascule? reglages = null, bool connusInconnus = false) =>
        new(T, declare, connusInconnus ? null : (connus ?? Quatre).ToHashSet(StringComparer.OrdinalIgnoreCase), (rapports ?? []).ToList(), s1,
            T.AddSeconds(-demarreIlYaSecondes), reglages ?? new ReglagesBascule());

    private static Serveur1Vu Joint => new(true, true, null, null);
    private static Serveur1Vu Muet(int depuisSecondes) => new(false, false, T.AddSeconds(-depuisSecondes), T.AddSeconds(-depuisSecondes));
    private static Serveur1Vu ApiMuette(int depuisSecondes) => new(true, false, null, T.AddSeconds(-depuisSecondes));

    // Poste qui perd le serveur 1 depuis « depuis » s, rapport reçu il y a 5 s
    private static RapportRetenu Perd(string poste, int depuis = 90, bool auth = true) => new(poste, false, T.AddSeconds(-5), T.AddSeconds(-depuis), auth);
    private static RapportRetenu Joint_(string poste, bool auth = true) => new(poste, true, T.AddSeconds(-5), null, auth);

    private static VerdictPromotion V(EntreeDecision e) => DecisionPromotion.Evaluer(e).Verdict;

    // ---- Inactif / informations manquantes ----
    [Fact] public void Officine_a_un_seul_serveur_inactif_meme_avec_tous_les_votes() =>
        Assert.Equal(VerdictPromotion.Inactif, V(Entree(rapports: Quatre.Select(p => Perd(p)), s1: Muet(600), declare: false)));

    [Fact] public void Postes_connus_inconnus_attendre() => Assert.Equal(VerdictPromotion.Attendre, V(Entree(connusInconnus: true, s1: Muet(600), rapports: [Perd("P1")])));
    [Fact] public void Aucun_poste_connu_attendre() => Assert.Equal(VerdictPromotion.Attendre, V(Entree(connus: [], s1: Muet(600), rapports: [Perd("P1")])));
    [Fact] public void Aucune_sonde_attendre() => Assert.Equal(VerdictPromotion.Attendre, V(Entree(rapports: Quatre.Select(p => Perd(p)))));

    // ---- Rester ----
    [Fact] public void Serveur1_joint_et_postes_qui_le_joignent_rester() =>
        Assert.Equal(VerdictPromotion.Rester, V(Entree(s1: Joint, rapports: Quatre.Select(p => Joint_(p)))));

    [Fact] public void Serveur1_joint_sans_rapport_rester() => Assert.Equal(VerdictPromotion.Rester, V(Entree(s1: Joint)));

    // ---- Cas 1 : promotion ----
    [Fact] public void Cas1_majorite_60_s_promotion() =>
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(s1: Muet(60), rapports: [Perd("P1"), Perd("P2"), Perd("P3")])));

    [Fact] public void Cas1_un_poste_authentifie_qui_joint_encore_le_serveur_1_bloque_la_promotion() =>
        Assert.Equal("poste_authentifie_joint_serveur1", DecisionPromotion.Evaluer(Entree(s1: Muet(120), rapports: [Perd("P1"), Perd("P2"), Perd("P3"), Joint_("P4")])).Raison);

    // ---- Revue de sécurité : seuls les rapports authentifiés comptent (promotion ET blocage) ----
    [Fact] public void Cas1_rapports_anonymes_ne_font_jamais_promouvoir()
    {
        var anonymes = Quatre.Select(p => Perd(p, 300, false)).ToList();
        var r = DecisionPromotion.Evaluer(Entree(s1: Muet(600), rapports: anonymes));
        Assert.NotEqual(VerdictPromotion.Promotion, r.Verdict);
        Assert.Equal(0, r.VotesAuthentifies);
    }

    [Fact] public void Cas1_un_faux_rapport_joint_anonyme_ne_bloque_pas_une_majorite_authentifiee() =>
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(s1: Muet(600), rapports: [Perd("P1"), Perd("P2"), Perd("P3"), Joint_("P4", auth: false), Joint_("X1", auth: false)])));

    [Fact] public void Cas1_des_votes_anonymes_ne_completent_pas_une_majorite_authentifiee_insuffisante() =>
        Assert.NotEqual(VerdictPromotion.Promotion, V(Entree(s1: Muet(600), rapports: [Perd("P1"), Perd("P2"), Perd("P3", 300, false), Perd("P4", 300, false)])));

    [Fact] public void Cas2_un_faux_rapport_joint_anonyme_ne_bloque_pas_le_retrait() =>
        Assert.Equal(VerdictPromotion.RetraitPuisPromotion, V(Entree(s1: Joint, rapports: [Perd("P1", 61), Perd("P2", 61), Perd("P3", 61), Joint_("P4", auth: false)])));

    [Fact] public void Cas1_un_seul_vote_sur_quatre_attendre() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(600), rapports: [Perd("P1")])));

    [Fact] public void Cas1_un_seul_poste_connu_un_vote_promotion() =>
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(connus: ["P1"], s1: Muet(90), rapports: [Perd("P1")])));

    [Fact] public void Cas1_egalite_pas_de_promotion_alerte()
    {
        var r = DecisionPromotion.Evaluer(Entree(s1: Muet(600), rapports: [Perd("P1"), Perd("P2")]));
        Assert.Equal(VerdictPromotion.Egalite, r.Verdict);
        Assert.Equal("egalite_majorite", r.TypeAlerte);
    }

    [Fact] public void Cas1_moins_de_60_s_attendre_meme_avec_majorite() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(59), rapports: [Perd("P1"), Perd("P2"), Perd("P3")])));

    [Fact] public void Cas1_moins_de_30_s_attendre() =>
        Assert.Equal("injoignable_depuis_peu", DecisionPromotion.Evaluer(Entree(s1: Muet(29), rapports: [Perd("P1"), Perd("P2"), Perd("P3")])).Raison);

    [Fact] public void Cas1_seuil_injoignable_regle_a_300_s_exige_300_s()
    {
        var r = new ReglagesBascule { InjoignableServeur1Secondes = 300 }.Borner();
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(299), rapports: [Perd("P1"), Perd("P2"), Perd("P3")], reglages: r)));
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(s1: Muet(300), rapports: [Perd("P1"), Perd("P2"), Perd("P3")], reglages: r)));
    }

    // ---- Plafonds de mémoire et de journal ----
    [Fact] public void Registre_borne_en_nombre_les_faux_noms_anonymes_n_evincent_jamais_un_poste_authentifie()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        reg.Enregistrer(Rapport("VRAI", false), "poste", true, T);
        for (var i = 0; i < RegistreRapportsPostes.PostesMax + 300; i++)
            reg.Enregistrer(Rapport("FAUX-" + i, false), "tiers-" + i, false, T.AddSeconds(1 + i * 0.001));
        Assert.True(reg.Rapports().Count <= RegistreRapportsPostes.PostesMax);
        Assert.True(reg.Dernier("VRAI")!.Authentifie);
        // Un nouveau rapport authentifié trouve toujours sa place
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("AUTRE-VRAI", false), "poste2", true, T.AddSeconds(5)));
        Assert.True(reg.Rapports().Count <= RegistreRapportsPostes.PostesMax);
    }

    [Fact] public async Task Journal_poste_deux_adresses_une_ligne_par_minute_et_par_poste_au_plus()
    {
        var (vigie, h, _, _, journal, _) = Monter(reference: T);
        await vigie.TraiterRapportAsync(Rapport("P1", false), "vrai", true);
        for (var i = 0; i < 6; i++)
        {
            h.Maintenant = h.Maintenant.AddSeconds(2);
            await vigie.TraiterRapportAsync(Rapport("P1", false), "tiers", false);
        }
        Assert.Single(journal.Evenements, e => e == "poste_deux_adresses");
        h.Maintenant = h.Maintenant.AddSeconds(61);
        await vigie.TraiterRapportAsync(Rapport("P1", false), "vrai", true);   // le vrai poste se manifeste de nouveau
        h.Maintenant = h.Maintenant.AddSeconds(2);
        await vigie.TraiterRapportAsync(Rapport("P1", false), "tiers", false);
        Assert.Equal(2, journal.Evenements.Count(e => e == "poste_deux_adresses"));
    }

    // ---- Contrainte de délais : PosteVu + Isolement + Sonde + marge < PromotionMinimum ----
    [Fact] public void Contrainte_de_delais_aux_bornes_la_promotion_minimum_est_relevee_au_minimum_sur()
    {
        // Valeurs par défaut : 45 + 20 + 5 + 5 = 75 < 76
        var d = new ReglagesBascule().Borner();
        Assert.Equal(76, d.PromotionMinimumSur);
        Assert.Equal(76, d.PromotionMinimumSecondes);
        Assert.True(d.PromotionMinimumRelevee);
        // Une valeur déjà sûre est gardée telle quelle
        var sur = new ReglagesBascule { PromotionMinimumSecondes = 200 }.Borner();
        Assert.Equal(200, sur.PromotionMinimumSecondes);
        Assert.False(sur.PromotionMinimumRelevee);
        // Exactement à la limite : 75 refusé (la contrainte est stricte), 76 accepté
        var limite = new ReglagesBascule { PromotionMinimumSecondes = 75 }.Borner();
        Assert.Equal(76, limite.PromotionMinimumSecondes);
        var juste = new ReglagesBascule { PromotionMinimumSecondes = 76 }.Borner();
        Assert.Equal(76, juste.PromotionMinimumSecondes);
        Assert.False(juste.PromotionMinimumRelevee);
        // Autres réglages aux bornes hautes : minimum sûr = 120 + 120 + 30 + 5 + 1, sous le plafond de 600
        var haut = new ReglagesBascule { PosteVuSecondes = 999, IsolementSecondes = 999, SondeSecondes = 999, PromotionMinimumSecondes = 60 }.Borner();
        Assert.Equal(276, haut.PromotionMinimumSecondes);
        Assert.True(haut.PromotionMinimumSecondes < 600);
        // Aux bornes basses : 30 + 10 + 2 + 5 + 1 = 48 < 60 : le plancher du contrat (60 s) reste le minimum
        var bas = new ReglagesBascule { PosteVuSecondes = 1, IsolementSecondes = 1, SondeSecondes = 1, PromotionMinimumSecondes = 1 }.Borner();
        Assert.Equal(60, bas.PromotionMinimumSecondes);
        Assert.False(bas.PromotionMinimumRelevee);
        // Idempotent
        Assert.Equal(76, d.Borner().PromotionMinimumSecondes);
        Assert.Equal(60, d.PromotionMinimumDemandee);
        // Réglages effectifs publiés pour comparaison entre les deux serveurs
        Assert.Equal(new ReglagesBascule().Borner().Empreinte(), d.Empreinte());
        Assert.NotEqual(new ReglagesBascule { IsolementSecondes = 30 }.Borner().Empreinte(), d.Empreinte());
    }

    [Fact] public void Seuils_regles_hors_bornes_ramenes_dans_15_s_a_5_min()
    {
        var bas = new ReglagesBascule { InjoignableServeur1Secondes = 1, PertePosteSecondes = 1, PromotionMinimumSecondes = 5 }.Borner();
        Assert.Equal(15, bas.InjoignableServeur1Secondes);
        Assert.Equal(15, bas.PertePosteSecondes);
        Assert.Equal(bas.PromotionMinimumSur, bas.PromotionMinimumSecondes);   // 60 s demandées < 45 + 20 + 5 + 5 : relevé au minimum sûr (76 s par défaut)
        Assert.True(bas.PromotionMinimumRelevee);
        Assert.Equal(60, bas.PromotionMinimumDemandee);
        var haut = new ReglagesBascule { InjoignableServeur1Secondes = 9999, PertePosteSecondes = 9999 }.Borner();
        Assert.Equal(300, haut.InjoignableServeur1Secondes);
        Assert.Equal(300, haut.PertePosteSecondes);
    }

    [Fact] public void Cas1_rapports_perimes_ne_votent_pas() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(600), rapports: Quatre.Select(p => new RapportRetenu(p, false, T.AddSeconds(-46), T.AddSeconds(-200), true)))));

    [Fact] public void Cas1_rapport_a_44_s_vote_encore() =>
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(s1: Muet(600), rapports: new[] { "P1", "P2", "P3" }.Select(p => new RapportRetenu(p, false, T.AddSeconds(-44), T.AddSeconds(-200), true)))));

    [Fact] public void Cas1_poste_inconnu_ne_vote_pas() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(600), rapports: [Perd("P1"), Perd("X1"), Perd("X2"), Perd("X3")])));

    [Fact] public void Cas1_nom_de_poste_insensible_a_la_casse() =>
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(connus: ["Caisse1", "Caisse2"], s1: Muet(600), rapports: [Perd("CAISSE1"), Perd("caisse2")])));

    [Fact] public void Cas1_demarrage_de_la_veille_aucune_promotion()
    {
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Muet(600), demarreIlYaSecondes: 119, rapports: [Perd("P1"), Perd("P2"), Perd("P3")])));
        Assert.Equal(VerdictPromotion.Promotion, V(Entree(s1: Muet(600), demarreIlYaSecondes: 120, rapports: [Perd("P1"), Perd("P2"), Perd("P3")])));
    }

    [Fact] public void Cas1_base_muette_mais_api_repond_ce_n_est_pas_une_perte_complete() =>
        Assert.NotEqual(VerdictPromotion.Promotion, V(Entree(s1: new Serveur1Vu(false, true, T.AddSeconds(-600), null), rapports: [Perd("P1"), Perd("P2"), Perd("P3")])));

    // ---- Q22 : base vivante, API muette ----
    [Fact] public void Q22_base_vivante_api_muette_pas_de_promotion_alerte()
    {
        var r = DecisionPromotion.Evaluer(Entree(s1: ApiMuette(600), rapports: Quatre.Select(p => Perd(p, auth: true))));
        Assert.Equal(VerdictPromotion.ApiServeur1Muette, r.Verdict);
        Assert.Equal("api_serveur1_arretee", r.TypeAlerte);
    }

    [Fact] public void Q22_api_muette_depuis_peu_attendre() => Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: ApiMuette(10), rapports: [Perd("P1")])));

    // ---- Cas 2 : retrait puis promotion ----
    [Fact] public void Cas2_serveur1_joint_aucun_poste_ne_le_joint_authentifies_60_s_retrait_puis_promotion() =>
        Assert.Equal(VerdictPromotion.RetraitPuisPromotion, V(Entree(s1: Joint, rapports: Quatre.Select(p => Perd(p, 60, true)))));

    [Fact] public void Cas2_majorite_authentifiee_seulement_suffit_si_aucun_poste_ne_joint() =>
        Assert.Equal(VerdictPromotion.RetraitPuisPromotion, V(Entree(s1: Joint, rapports: new[] { "P1", "P2", "P3" }.Select(p => Perd(p, 61, true)))));

    [Fact] public void Cas2_moins_de_60_s_attendre() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Joint, rapports: Quatre.Select(p => Perd(p, 59, true)))));

    [Fact] public void Cas2_duree_comptee_depuis_la_perte_du_dernier_poste_necessaire_a_la_majorite() =>
        // 4 connus : il faut 3 postes ; les trois perdent depuis 200, 100 et 10 s : la majorité n'existe que depuis 10 s
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Joint, rapports: [Perd("P1", 200, true), Perd("P2", 100, true), Perd("P3", 10, true)])));

    [Fact] public void Cas2_q23_rapports_sans_employe_connecte_ne_comptent_pas_rester() =>
        Assert.Equal(VerdictPromotion.Rester, V(Entree(s1: Joint, rapports: Quatre.Select(p => Perd(p, 300, false)))));

    [Fact] public void Cas2_q23_deux_authentifies_sur_quatre_ne_suffisent_pas() =>
        Assert.Equal(VerdictPromotion.Relais, V(Entree(s1: Joint, rapports: [Perd("P1", 300, true), Perd("P2", 300, true), Perd("P3", 300, false), Perd("P4", 300, false)])));

    [Fact] public void Cas2_un_poste_joint_le_serveur_1_relais_jamais_de_retrait() =>
        Assert.Equal(VerdictPromotion.Relais, V(Entree(s1: Joint, rapports: [Perd("P1", 300, true), Perd("P2", 300, true), Perd("P3", 300, true), Joint_("P4")])));

    [Fact] public void Cas2_un_poste_inconnu_qui_joint_le_serveur_1_empeche_le_retrait() =>
        Assert.Equal(VerdictPromotion.Relais, V(Entree(s1: Joint, rapports: [Perd("P1", 300, true), Perd("P2", 300, true), Perd("P3", 300, true), Joint_("X9")])));

    [Fact] public void Cas2_demarrage_de_la_veille_aucun_retrait() =>
        Assert.Equal(VerdictPromotion.Attendre, V(Entree(s1: Joint, demarreIlYaSecondes: 30, rapports: Quatre.Select(p => Perd(p, 300, true)))));

    // ---- Cas 3 : relais ----
    [Fact] public void Cas3_quelques_postes_seulement_relais() =>
        Assert.Equal(VerdictPromotion.Relais, V(Entree(s1: Joint, rapports: [Perd("P1", 300, true), Joint_("P2"), Joint_("P3"), Joint_("P4")])));

    // ---- Consignes ----
    [Theory]
    [InlineData(VerdictPromotion.Relais, false, "relais")]
    [InlineData(VerdictPromotion.Relais, true, "rester")]
    [InlineData(VerdictPromotion.Attendre, false, "attendre")]
    [InlineData(VerdictPromotion.Attendre, true, "rester")]
    [InlineData(VerdictPromotion.Rester, false, "rester")]
    [InlineData(VerdictPromotion.Inactif, false, "rester")]
    [InlineData(VerdictPromotion.Promotion, false, "attendre")]
    [InlineData(VerdictPromotion.RetraitPuisPromotion, false, "attendre")]
    [InlineData(VerdictPromotion.ApiServeur1Muette, false, "attendre")]
    [InlineData(VerdictPromotion.Egalite, false, "attendre")]
    public void Consigne_selon_verdict_et_poste(VerdictPromotion v, bool joint, string attendue) => Assert.Equal(attendue, ConsigneDuVerdict.Pour(v, joint));

    // Essai réel V2-8 (2026-11-25) : le 2e serveur promu (principal) doit dire « basculer » aux postes qui ont perdu leur serveur
    [Theory]
    [InlineData(true, false, false, "basculer")]   // promu, poste qui a perdu le serveur 1
    [InlineData(true, false, true, "rester")]      // poste qui joint encore son serveur : aucune bascule
    [InlineData(true, true, false, null)]          // serveur écarté : jamais de consigne (404)
    [InlineData(false, false, false, null)]        // officine à un seul serveur : pas de vigie (404)
    public void Consigne_d_un_serveur_principal(bool deuxServeurs, bool ecarte, bool joint, string? attendue) =>
        Assert.Equal(attendue, ConsigneDuVerdict.PourPrincipal(deuxServeurs, ecarte, joint));

    // ---- Contrat : valeurs de fil et défauts alignés sur Pharma.Shared ----
    [Fact] public void Valeurs_de_fil_et_defauts_alignes_sur_le_contrat()
    {
        Assert.Equal(ConsigneVigie.Rester, ConstantesVigie.Rester);
        Assert.Equal(ConsigneVigie.Attendre, ConstantesVigie.Attendre);
        Assert.Equal(ConsigneVigie.Relais, ConstantesVigie.Relais);
        Assert.Equal(ConsigneVigie.Basculer, ConstantesVigie.Basculer);
        Assert.Equal(RoleServeur.Veille, ConstantesVigie.RoleVeille);
        Assert.Equal(TypeAlerteInterServeurs.ApiServeur1Arretee, ConstantesVigie.AlerteApiServeur1Arretee);
        Assert.Equal(TypeAlerteInterServeurs.EgaliteMajorite, ConstantesVigie.AlerteEgaliteMajorite);
        var r = new ReglagesBascule();
        Assert.Equal(ReglagesBasculeParDefaut.PertePosteSecondes, r.PertePosteSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.InjoignableServeur1Secondes, r.InjoignableServeur1Secondes);
        Assert.Equal(ReglagesBasculeParDefaut.IsolementServeur1Secondes, r.IsolementSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.PromotionMinimumSecondes, r.PromotionMinimumSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.RetourVoieDirecteSecondes, r.RetourVoieDirecteSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.PosteActifMinutes, r.PosteActifMinutes);
        Assert.Equal(ReglagesBasculeParDefaut.PosteVuSecondes, r.PosteVuSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.DemarrageVeilleSansDecisionSecondes, r.DemarrageVeilleSansDecisionSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.SondeSecondes, r.SondeSecondes);
        Assert.Equal(ReglagesBasculeParDefaut.FenetreSignatureSecondes, r.FenetreSignatureSecondes);
    }

    // ---- Postes connus : référence = dernière donnée reçue ----
    [Fact] public void Postes_connus_calcules_par_rapport_a_la_derniere_donnee_recue_pas_a_l_heure_courante()
    {
        var postes = new[] { new PosteVuEnBase("P1", T.AddSeconds(-30)), new PosteVuEnBase("P2", T.AddSeconds(-170)), new PosteVuEnBase("P3", T.AddSeconds(-181)) };
        var connus = PostesConnus.Calculer(postes, T, TimeSpan.FromMinutes(3))!;
        Assert.Equal(["P1", "P2"], connus.OrderBy(x => x));
        // Dix minutes plus tard la copie est figée : la référence reste T, la liste ne fond pas
        Assert.Equal(2, PostesConnus.Calculer(postes, T, TimeSpan.FromMinutes(3))!.Count);
        // Le piège : avec l'heure courante la liste serait vide
        Assert.Empty(PostesConnus.Calculer(postes, T.AddMinutes(10), TimeSpan.FromMinutes(3))!);
    }

    [Fact] public void Postes_connus_reference_inconnue_donne_null() => Assert.Null(PostesConnus.Calculer([new PosteVuEnBase("P1", T)], null, TimeSpan.FromMinutes(3)));

    [Fact] public void Postes_connus_fenetre_reglable()
    {
        var postes = new[] { new PosteVuEnBase("P1", T.AddMinutes(-8)) };
        Assert.Empty(PostesConnus.Calculer(postes, T, TimeSpan.FromMinutes(3))!);
        Assert.Single(PostesConnus.Calculer(postes, T, TimeSpan.FromMinutes(10))!);
    }

    // ---- Registre des rapports ----
    private static RapportPoste Rapport(string poste, bool joint) => new(poste, joint, null, "1.0", "https://serveur1:5080");

    [Fact] public void Registre_plus_d_un_rapport_par_seconde_par_adresse_refuse()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", true), "10.0.0.5", false, T));
        Assert.Equal(IssueRapport.TropRapide, reg.Enregistrer(Rapport("P1", true), "10.0.0.5", false, T.AddMilliseconds(500)));
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", true), "10.0.0.5", false, T.AddSeconds(1)));
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P2", true), "10.0.0.6", false, T.AddSeconds(1)));
    }

    [Fact] public void Registre_nom_de_poste_annonce_par_deux_adresses_ignore()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", false), "10.0.0.5", false, T));
        Assert.Equal(IssueRapport.PosteDeuxAdresses, reg.Enregistrer(Rapport("P1", true), "10.0.0.9", false, T.AddSeconds(5)));
        Assert.False(reg.Dernier("P1")!.Serveur1Joint);   // rapport de l'autre adresse non retenu
        // Après la fenêtre de 45 s, le poste a changé d'adresse (DHCP, redémarrage) : accepté
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", true), "10.0.0.9", false, T.AddSeconds(50)));
    }

    // ---- Revue de sécurité : un rapport authentifié remplace l'anonyme, jamais l'inverse ----
    [Fact] public void Registre_un_rapport_authentifie_remplace_un_rapport_anonyme_du_meme_nom_venu_d_une_autre_adresse()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", true), "tiers", false, T));
        Assert.Equal(IssueRapport.PosteDeuxAdresses, reg.Enregistrer(Rapport("P1", false), "autre-tiers", false, T.AddSeconds(5)));
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", false), "vrai-poste", true, T.AddSeconds(10)));
        var r = reg.Dernier("P1")!;
        Assert.True(r.Authentifie);
        Assert.False(r.Serveur1Joint);
    }

    [Fact] public void Registre_un_rapport_anonyme_ne_remplace_jamais_un_rapport_authentifie_recent()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        reg.Enregistrer(Rapport("P1", false), "vrai-poste", true, T);
        Assert.Equal(IssueRapport.PosteDeuxAdresses, reg.Enregistrer(Rapport("P1", true), "tiers", false, T.AddSeconds(5)));
        Assert.Equal(IssueRapport.AuthentifieRecent, reg.Enregistrer(Rapport("P1", true), "vrai-poste", false, T.AddSeconds(10)));
        Assert.False(reg.Dernier("P1")!.Serveur1Joint);
        Assert.True(reg.Dernier("P1")!.Authentifie);
        // Au-delà de la fenêtre de fraîcheur, le rapport anonyme est de nouveau accepté
        Assert.Equal(IssueRapport.Accepte, reg.Enregistrer(Rapport("P1", true), "vrai-poste", false, T.AddSeconds(60)));
    }

    [Fact] public void Registre_un_tiers_qui_annonce_200_noms_de_postes_n_ajoute_aucun_vote_authentifie()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        for (var i = 0; i < 200; i++) reg.Enregistrer(Rapport("FAUX-" + i, false), "tiers-" + i, false, T.AddSeconds(i * 0.01));
        Assert.All(reg.Rapports(), r => Assert.False(r.Authentifie));
        var connus = Quatre.ToHashSet(StringComparer.OrdinalIgnoreCase);   // les postes connus viennent de la base, jamais des rapports
        var res = DecisionPromotion.Evaluer(new EntreeDecision(T.AddSeconds(5), true, connus, reg.Rapports(), Muet(600), T.AddHours(-1), new ReglagesBascule()));
        Assert.NotEqual(VerdictPromotion.Promotion, res.Verdict);
        Assert.NotEqual(VerdictPromotion.RetraitPuisPromotion, res.Verdict);
        Assert.Equal(0, res.VotesAuthentifies);
    }

    [Fact] public void Registre_la_perte_est_datee_par_le_serveur_et_remise_a_zero_quand_le_poste_rejoint()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        reg.Enregistrer(Rapport("P1", false), "a", false, T);
        reg.Enregistrer(Rapport("P1", false), "a", true, T.AddSeconds(10));
        Assert.Equal(T, reg.Dernier("P1")!.PerteDepuisUtc);
        Assert.True(reg.Dernier("P1")!.Authentifie);
        reg.Enregistrer(Rapport("P1", true), "a", true, T.AddSeconds(20));
        Assert.Null(reg.Dernier("P1")!.PerteDepuisUtc);
        reg.Enregistrer(Rapport("P1", false), "a", true, T.AddSeconds(30));
        Assert.Equal(T.AddSeconds(30), reg.Dernier("P1")!.PerteDepuisUtc);
    }

    [Fact] public void Registre_oublie_les_rapports_de_plus_de_10_minutes()
    {
        var reg = new RegistreRapportsPostes(new ReglagesBascule());
        reg.Enregistrer(Rapport("P1", true), "a", false, T);
        reg.Enregistrer(Rapport("P2", true), "b", false, T.AddMinutes(11));
        Assert.Null(reg.Dernier("P1"));
        Assert.NotNull(reg.Dernier("P2"));
    }

    // ---- Vigie : bout en bout avec horloge simulée ----
    private sealed class HorlogeSimulee(DateTime depart) : IHorloge
    {
        public DateTime Maintenant { get; set; } = depart;
        public DateTime MaintenantUtc => Maintenant;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(Maintenant);
    }

    private sealed class ReleveFactice(bool regle, bool joint) : IReleveServeurs
    {
        public bool Joint { get; set; } = joint;
        public bool AutreServeurRegle => regle;
        public string? AutreServeur => regle ? "serveur1" : null;
        public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) => Task.FromResult<ReleveServeur?>(new ReleveServeur(true, null, null));
        public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) => Task.FromResult(Joint ? new ReleveServeur(false, 1, 1) : null);
    }

    private sealed class ClientFactice(bool joint) : ILecteurEtatServeur1
    {
        public bool Joint { get; set; } = joint;
        public Task<EtatServeur1?> EtatServeur1Async(CancellationToken ct = default) =>
            Task.FromResult(Joint ? new EtatServeur1("s1", "principal", 1, false, "1", null) : null);
    }

    private sealed class DepotFige(DateTime? reference, params PosteVuEnBase[] postes) : IPostesConnusDepot
    {
        public Task<PostesConnusLus> LireAsync(CancellationToken ct = default) => Task.FromResult(new PostesConnusLus(reference, postes));
    }

    private sealed class JournalMemoire : IJournalDecisions
    {
        public List<string> Evenements { get; } = [];
        public Task EcrireAsync(string evenement, object donnees, CancellationToken ct = default) { Evenements.Add(evenement); return Task.CompletedTask; }
    }

    private static (VigieSecondServeur Vigie, HorlogeSimulee H, ReleveFactice Releve, ClientFactice Client, JournalMemoire Journal, RegistreRapportsPostes Reg) Monter(
        DateTime? reference, bool declare = true, int nbConnus = 4)
    {
        var h = new HorlogeSimulee(T);
        var releve = new ReleveFactice(declare, true);
        var client = new ClientFactice(true);
        var journal = new JournalMemoire();
        var reglages = new ReglagesBascule();
        var reg = new RegistreRapportsPostes(reglages);
        var postes = Enumerable.Range(1, nbConnus).Select(i => new PosteVuEnBase($"P{i}", T.AddSeconds(-20))).ToArray();
        var vigie = new VigieSecondServeur(reg, new DepotFige(reference, postes), new SondeServeur1(releve, client), releve, journal, h, reglages);
        h.Maintenant = T.AddSeconds(200);   // le démarrage de la veille est passé (120 s)
        return (vigie, h, releve, client, journal, reg);
    }

    private static async Task Tour(VigieSecondServeur v, HorlogeSimulee h, RegistreRapportsPostes reg, int secondes, params (string Poste, string Adresse)[] perdants)
    {
        for (var i = 0; i < secondes; i += 5)
        {
            h.Maintenant = h.Maintenant.AddSeconds(5);
            foreach (var (poste, adresse) in perdants)
                await v.TraiterRapportAsync(Rapport(poste, false), adresse, true);
            await v.EvaluerAsync();
        }
    }

    [Fact] public async Task Piege_copie_figee_dix_minutes_un_seul_vote_sur_quatre_postes_connus_jamais_de_promotion()
    {
        var (vigie, h, releve, client, _, reg) = Monter(reference: T);   // dernière donnée reçue : T, figée ; les postes connus ont été vus à T-20 s
        await vigie.EvaluerAsync();
        Assert.Equal(VerdictPromotion.Rester, vigie.Dernier!.Verdict);
        releve.Joint = false; client.Joint = false;   // coupure du serveur 1
        var verdicts = new HashSet<VerdictPromotion>();
        for (var i = 0; i < 120; i++)   // 10 minutes simulées, un tour toutes les 5 s, le poste P1 vote toutes les 5 s
        {
            await Tour(vigie, h, reg, 5, ("P1", "10.0.0.1"));
            verdicts.Add(vigie.Dernier!.Verdict);
            Assert.Equal(4, vigie.Dernier.Connus);   // la liste ne fond JAMAIS
            Assert.Equal(1, vigie.Dernier.Votes);
        }
        Assert.DoesNotContain(VerdictPromotion.Promotion, verdicts);
        Assert.DoesNotContain(VerdictPromotion.RetraitPuisPromotion, verdicts);
        Assert.True(h.Maintenant - T >= TimeSpan.FromMinutes(10));
    }

    [Fact] public async Task Coupure_puis_trois_postes_sur_quatre_votent_promotion_apres_60_s_et_confirmations_comptees()
    {
        var (vigie, h, releve, client, journal, reg) = Monter(reference: T);
        await vigie.EvaluerAsync();
        releve.Joint = false; client.Joint = false;
        await Tour(vigie, h, reg, 55, ("P1", "a"), ("P2", "b"), ("P3", "c"));
        Assert.Equal(VerdictPromotion.Attendre, vigie.Dernier!.Verdict);   // 55 s : pas encore 60 s
        await Tour(vigie, h, reg, 15, ("P1", "a"), ("P2", "b"), ("P3", "c"));
        Assert.Equal(VerdictPromotion.Promotion, vigie.Dernier!.Verdict);
        Assert.True(vigie.Confirmations >= 1);
        await Tour(vigie, h, reg, 5, ("P1", "a"), ("P2", "b"), ("P3", "c"));
        Assert.True(vigie.Confirmations >= 2);   // « décision confirmée 2 fois à 5 s » lue par V2-4
        Assert.Contains("verdict", journal.Evenements);
    }

    [Fact] public async Task Copie_sans_derniere_donnee_connue_aucune_decision()
    {
        var (vigie, h, releve, client, _, reg) = Monter(reference: null);
        releve.Joint = false; client.Joint = false;
        await Tour(vigie, h, reg, 300, ("P1", "a"), ("P2", "b"), ("P3", "c"), ("P4", "d"));
        Assert.Equal(VerdictPromotion.Attendre, vigie.Dernier!.Verdict);
        Assert.Equal("postes_connus_inconnus", vigie.Dernier.Raison);
    }

    [Fact] public async Task Officine_a_un_seul_serveur_vigie_inactive_et_consigne_rester()
    {
        var (vigie, h, _, _, journal, reg) = Monter(reference: T, declare: false);
        await Tour(vigie, h, reg, 300, ("P1", "a"), ("P2", "b"), ("P3", "c"), ("P4", "d"));
        Assert.Equal(VerdictPromotion.Inactif, vigie.Dernier!.Verdict);
        var rep = await vigie.TraiterRapportAsync(Rapport("P1", false), "z", false);
        Assert.Equal("rester", rep.Consigne!.Consigne);
    }

    [Fact] public async Task Rapport_consigne_attendre_avant_toute_evaluation_puis_relais_pour_le_poste_qui_perd()
    {
        var (vigie, h, _, client, _, reg) = Monter(reference: T);
        var avant = await vigie.TraiterRapportAsync(Rapport("P1", false), "a", false);
        Assert.Equal("attendre", avant.Consigne!.Consigne);
        Assert.Equal("veille", avant.Consigne.Role);
        Assert.Equal(ConsigneDuVerdict.MessageAttente, avant.Consigne.Message);
        // Serveur 1 joint par le 2e serveur, un seul poste le perd : relais pour lui seulement
        h.Maintenant = h.Maintenant.AddSeconds(5);
        await vigie.TraiterRapportAsync(Rapport("P2", true), "b", true);
        await vigie.TraiterRapportAsync(Rapport("P1", false), "a", true);   // « relais » ne repose que sur des rapports authentifiés
        await vigie.EvaluerAsync();
        Assert.Equal(VerdictPromotion.Relais, vigie.Dernier!.Verdict);
        h.Maintenant = h.Maintenant.AddSeconds(2);   // plus d'un rapport par seconde et par adresse serait refusé
        Assert.Equal("relais", (await vigie.TraiterRapportAsync(Rapport("P1", false), "a", true)).Consigne!.Consigne);
        Assert.Equal("rester", (await vigie.TraiterRapportAsync(Rapport("P2", true), "b", false)).Consigne!.Consigne);
        Assert.NotNull(client);
    }

    [Fact] public async Task Rapport_trop_rapide_et_rapport_de_deux_adresses_sont_signales()
    {
        var (vigie, h, _, _, journal, _) = Monter(reference: T);
        Assert.Equal(StatutRapport.Accepte, (await vigie.TraiterRapportAsync(Rapport("P1", true), "a", false)).Statut);
        Assert.Equal(StatutRapport.TropRapide, (await vigie.TraiterRapportAsync(Rapport("P1", true), "a", false)).Statut);
        h.Maintenant = h.Maintenant.AddSeconds(2);
        Assert.Equal(StatutRapport.Ignore, (await vigie.TraiterRapportAsync(Rapport("P1", true), "b", false)).Statut);
        Assert.Contains("poste_deux_adresses", journal.Evenements);
    }

    [Fact] public async Task Cas2_bout_en_bout_serveur1_joint_postes_authentifies_retrait_puis_promotion()
    {
        var (vigie, h, _, _, _, reg) = Monter(reference: T);
        for (var i = 0; i < 16; i++)   // 80 s de rapports « perdu » authentifiés, le 2e serveur joint le serveur 1
        {
            h.Maintenant = h.Maintenant.AddSeconds(5);
            foreach (var (p, a) in new[] { ("P1", "a"), ("P2", "b"), ("P3", "c") })
                await vigie.TraiterRapportAsync(Rapport(p, false), a, true);
            await vigie.EvaluerAsync();
        }
        Assert.Equal(VerdictPromotion.RetraitPuisPromotion, vigie.Dernier!.Verdict);
        Assert.NotNull(reg);
    }
}
