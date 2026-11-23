namespace Pharma.Application.SecondServeur;

// ---- Bascule du 2e serveur, vague 2, lot V2-3 : décision de promotion (2026-11-23) --------------------------------------------
// Fonction PURE (aucune horloge, aucune base, aucun réseau : tout vient de l'entrée). Elle expose un VERDICT ; elle ne promeut rien
// (promotion et retrait : lot V2-4). Règles : contrat-api.md § 3 et plan-vague-2.md § 3 et § 16.

/// <summary>Valeurs de fil du contrat (Pharma.Shared n'est pas visible d'Application : un test vérifie l'égalité avec les constantes de Pharma.Shared).</summary>
public static class ConstantesVigie
{
    public const string Rester = "rester", Attendre = "attendre", Relais = "relais", Basculer = "basculer";
    public const string RoleVeille = "veille";
    public const string AlerteApiServeur1Arretee = "api_serveur1_arretee", AlerteEgaliteMajorite = "egalite_majorite";
}

public enum VerdictPromotion
{
    /// <summary>Aucun 2e serveur déclaré : mécanisme inactif.</summary>
    Inactif,
    /// <summary>Le serveur 1 est joint et aucun poste ne le perd.</summary>
    Rester,
    /// <summary>Information insuffisante ou conditions pas encore tenues assez longtemps : aucune action.</summary>
    Attendre,
    /// <summary>Cas 3 : quelques postes seulement perdent le serveur 1 ; le 2e serveur les relaie.</summary>
    Relais,
    /// <summary>Cas 2 : le 2e serveur joint le serveur 1, aucun poste ne le joint ; retrait du serveur 1 puis promotion (3 preuves : V2-4).</summary>
    RetraitPuisPromotion,
    /// <summary>Cas 1 : serveur 1 injoignable (base + API), majorité de postes qui le perdent, 60 s écoulées.</summary>
    Promotion,
    /// <summary>Q22 : base du serveur 1 vivante mais API muette. Jamais de promotion ; alerte « api_serveur1_arretee ».</summary>
    ApiServeur1Muette,
    /// <summary>Votes = moitié exactement des postes connus : pas de promotion ; alerte « egalite_majorite ».</summary>
    Egalite
}

/// <summary>Dernier rapport retenu d'un poste. PerteDepuisUtc : horloge DU SERVEUR (début de la série de rapports « serveur 1 perdu »), null si joint.</summary>
public sealed record RapportRetenu(string Poste, bool Serveur1Joint, DateTime RecuLeUtc, DateTime? PerteDepuisUtc, bool Authentifie);

/// <summary>Serveur 1 vu par le 2e serveur. Muette depuis : null tant que la sonde répond.</summary>
public sealed record Serveur1Vu(bool BaseJointe, bool ApiJointe, DateTime? BaseMuetteDepuisUtc, DateTime? ApiMuetteDepuisUtc);

/// <param name="PostesConnus">Postes connus calculés sur la copie par <see cref="PostesConnus.Calculer"/> ; null = inconnus (aucune décision possible).</param>
/// <param name="Serveur1">Null = aucune sonde encore faite.</param>
public sealed record EntreeDecision(
    DateTime MaintenantUtc, bool SecondServeurDeclare, IReadOnlySet<string>? PostesConnus, IReadOnlyList<RapportRetenu> Rapports,
    Serveur1Vu? Serveur1, DateTime DemarrageVeilleUtc, ReglagesBascule Reglages);

public sealed record ResultatDecision(VerdictPromotion Verdict, string Raison, int Connus, int Votes, int VotesAuthentifies, int Joignent, string? TypeAlerte);

public static class DecisionPromotion
{
    public static ResultatDecision Evaluer(EntreeDecision e)
    {
        if (!e.SecondServeurDeclare) return new(VerdictPromotion.Inactif, "aucun_second_serveur_declare", 0, 0, 0, 0, null);
        if (e.PostesConnus is null) return new(VerdictPromotion.Attendre, "postes_connus_inconnus", 0, 0, 0, 0, null);
        var r = e.Reglages;
        var connus = e.PostesConnus;
        var c = connus.Count;
        var fenetre = TimeSpan.FromSeconds(r.PosteVuSecondes);

        // Rapports récents (moins de PosteVuSecondes). Votes : postes CONNUS qui disent « serveur 1 non joint ». Un poste qui le joint ne vote jamais.
        var recents = e.Rapports.Where(x => e.MaintenantUtc - x.RecuLeUtc < fenetre).ToList();
        var votants = recents.Where(x => !x.Serveur1Joint && connus.Contains(x.Poste)).ToList();
        var authentifies = votants.Where(x => x.Authentifie).ToList();
        // Prudence : un poste qui joint le serveur 1 compte comme « joignant », connu ou non (jamais de retrait ni de promotion alors).
        var joignent = recents.Count(x => x.Serveur1Joint);
        int votes = votants.Count, votesAuth = authentifies.Count;

        ResultatDecision Res(VerdictPromotion v, string raison, string? alerte = null) => new(v, raison, c, votes, votesAuth, joignent, alerte);

        if (c == 0) return Res(VerdictPromotion.Attendre, "aucun_poste_connu");
        if (e.Serveur1 is not { } s) return Res(VerdictPromotion.Attendre, "aucune_sonde");

        var demarrage = e.MaintenantUtc - e.DemarrageVeilleUtc < TimeSpan.FromSeconds(r.DemarrageVeilleSansDecisionSecondes);
        var injoignable = TimeSpan.FromSeconds(r.InjoignableServeur1Secondes);
        var dureeTotale = TimeSpan.FromSeconds(Math.Max(r.InjoignableServeur1Secondes, r.PromotionMinimumSecondes));

        // Q22 : base vivante, API muette -> relance de l'API, jamais une promotion
        if (s.BaseJointe && !s.ApiJointe)
            return s.ApiMuetteDepuisUtc is { } muette && e.MaintenantUtc - muette >= injoignable
                ? Res(VerdictPromotion.ApiServeur1Muette, "base_vivante_api_muette", ConstantesVigie.AlerteApiServeur1Arretee)
                : Res(VerdictPromotion.Attendre, "api_muette_depuis_peu");

        // Cas 1 : base ET API muettes
        if (!s.BaseJointe && !s.ApiJointe)
        {
            if (s.BaseMuetteDepuisUtc is not { } bm || s.ApiMuetteDepuisUtc is not { } am) return Res(VerdictPromotion.Attendre, "muet_depuis_inconnu");
            var depuis = bm > am ? bm : am;   // la perte est complète depuis le plus récent des deux silences
            if (e.MaintenantUtc - depuis < injoignable) return Res(VerdictPromotion.Attendre, "injoignable_depuis_peu");
            if (votes * 2 == c) return Res(VerdictPromotion.Egalite, "votes_egaux_moitie", ConstantesVigie.AlerteEgaliteMajorite);
            if (votes * 2 < c) return Res(VerdictPromotion.Attendre, "majorite_non_atteinte");
            if (e.MaintenantUtc - depuis < dureeTotale) return Res(VerdictPromotion.Attendre, "delai_total_non_atteint");
            if (demarrage) return Res(VerdictPromotion.Attendre, "demarrage_veille");
            return Res(VerdictPromotion.Promotion, "cas_1_injoignable_majorite");
        }

        // Le 2e serveur joint le serveur 1 (son API répond)
        if (votes == 0) return Res(VerdictPromotion.Rester, "serveur1_joint_aucun_vote");

        // Cas 2 : aucun poste ne joint le serveur 1 et MAJORITÉ de rapports authentifiés (Q23 : employé connecté)
        if (joignent == 0 && votesAuth * 2 > c)
        {
            // Durée : depuis que la majorité authentifiée perd le serveur 1 (instant où le dernier poste nécessaire à la majorité l'a perdu)
            var instants = authentifies.Select(x => x.PerteDepuisUtc ?? x.RecuLeUtc).OrderBy(x => x).ToList();
            var depuis = instants[c / 2];   // indice (c/2 + 1) - 1 : plus petite majorité stricte
            if (e.MaintenantUtc - depuis < TimeSpan.FromSeconds(r.PromotionMinimumSecondes)) return Res(VerdictPromotion.Attendre, "delai_total_non_atteint");
            if (demarrage) return Res(VerdictPromotion.Attendre, "demarrage_veille");
            return Res(VerdictPromotion.RetraitPuisPromotion, "cas_2_serveur1_joint_aucun_poste");
        }

        // Cas 3 : quelques postes seulement (ou rapports non authentifiés) -> relais, jamais de promotion
        return Res(VerdictPromotion.Relais, "cas_3_quelques_postes");
    }
}

/// <summary>Consigne renvoyée à un poste d'après le verdict en cours et ce que ce poste rapporte (« basculer » est posé par le lot V2-4 après promotion).</summary>
public static class ConsigneDuVerdict
{
    public const string MessageAttente = "Vérification avec le 2e serveur en cours";

    public static string Pour(VerdictPromotion verdict, bool posteJoint) =>
        posteJoint ? ConstantesVigie.Rester
        : verdict switch
        {
            VerdictPromotion.Relais => ConstantesVigie.Relais,
            VerdictPromotion.Inactif or VerdictPromotion.Rester => ConstantesVigie.Rester,
            _ => ConstantesVigie.Attendre
        };
}
