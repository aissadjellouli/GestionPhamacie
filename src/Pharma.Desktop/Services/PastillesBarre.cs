using System.Globalization;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Pastille de la barre d'état : libellé court, niveau (null : à traiter, gris ; « attention » : orange ; « anormal » : rouge), écran ouvert
/// par un clic (null : texte simple, l'employé n'a pas le droit de l'ouvrir), infobulle.
/// </summary>
public sealed record Pastille(string Cle, string Libelle, string? Niveau, string? Lien, string? Titre = null);

/// <summary>
/// Ce que sait le poste au moment de dessiner la barre. Les dates reçues du serveur sont converties en heure locale ; Maintenant est l'heure
/// locale du poste. SecondesAvantDeconnexion : temps restant avant la déconnexion automatique (null : pas de déconnexion automatique).
/// </summary>
public sealed record EtatBarre(bool ServeurJoignable, DateTime Maintenant, Func<string, bool> Droit, EtatPosteDto? Poste = null,
    EtatLiaisonsPosteDto? Liaisons = null, CompteursBarreDto? Compteurs = null, int? SecondesAvantDeconnexion = null, string? ErreurImprimante = null);

/// <summary>
/// Pastilles à dessiner. Regroupe : au moins une pastille orange ou rouge est affichée, les pastilles grises passent dans « À traiter (N) »
/// (avec les anomalies au-delà de <see cref="PastillesBarre.AnomaliesVisiblesMax"/>). NiveauGroupe : niveau le plus grave de la liste,
/// repris par le bouton « À traiter » (une anomalie rangée dans la liste reste signalée).
/// </summary>
public sealed record AffichageBarre(IReadOnlyList<Pastille> Visibles, IReadOnlyList<Pastille> ATraiter)
{
    public bool Regroupe => ATraiter.Count > 0;
    public string? NiveauGroupe => ATraiter.Any(p => p.Niveau == PastillesBarre.Anormal) ? PastillesBarre.Anormal
        : ATraiter.Any(p => p.Niveau == PastillesBarre.Attention) ? PastillesBarre.Attention : null;
}

/// <summary>
/// Règles de la barre d'état (maquette validée le 2026-11-21) : une pastille seulement s'il y a une information, cliquable vers l'écran
/// concerné pour qui a le droit de l'ouvrir. Serveur injoignable : seules restent les pastilles qui ne dépendent pas du serveur
/// (imprimante de tickets, déconnexion automatique). Sans dépendance à l'interface (testées).
/// </summary>
public static class PastillesBarre
{
    public const string Attention = "attention";
    public const string Anormal = "anormal";

    /// <summary>Déconnexion automatique annoncée dans les 5 dernières minutes.</summary>
    public const int PreavisDeconnexionSecondes = 5 * 60;

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    /// <summary>
    /// Anomalies affichées au plus dans la barre : 4 tiennent sur une ligne en 1366 px avec « À traiter » et les éléments fixes (cas le
    /// plus chargé de la maquette, mesuré le 2026-11-21) ; au-delà, les moins graves passent dans la liste « À traiter ».
    /// </summary>
    public const int AnomaliesVisiblesMax = 4;

    public static AffichageBarre Calculer(EtatBarre e)
    {
        var toutes = Toutes(e).ToList();
        var anomalies = toutes.Where(p => p.Niveau is not null).ToList();
        var grises = toutes.Where(p => p.Niveau is null).ToList();
        if (anomalies.Count == 0 || (grises.Count == 0 && anomalies.Count <= AnomaliesVisiblesMax)) return new AffichageBarre(toutes, []);
        // Rouges d'abord pour choisir celles qui restent visibles ; l'ordre de la barre est gardé
        var gardees = anomalies.Select((p, i) => (p, i)).OrderBy(x => x.p.Niveau == Anormal ? 0 : 1).ThenBy(x => x.i)
            .Take(AnomaliesVisiblesMax).OrderBy(x => x.i).Select(x => x.p).ToList();
        return new AffichageBarre(gardees, anomalies.Except(gardees).Concat(grises).ToList());
    }

    /// <summary>Poste et vendeur connecté (« Caisse 1 · Karim ») ; le poste reçu est PHARMA_POSTE, sinon le nom de l'ordinateur (ConfigurationPoste.NomCourt) ; partie vide omise.</summary>
    public static string Identite(string? posteDeclare, string? vendeur) =>
        string.Join(" · ", new[] { posteDeclare, vendeur }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));

    /// <summary>Date et heure à droite : « sam. 21/11 · 10:42 », raccourcie « 21/11 · 10:42 » quand les pastilles sont regroupées.</summary>
    public static string Date(DateTime maintenant, bool courte) => maintenant.ToString(courte ? "dd/MM · HH:mm" : "ddd dd/MM · HH:mm", Fr);

    private static DateTime Local(DateTime d) => d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d;

    private static string Pluriel(int n, string un, string plusieurs) => n == 1 ? $"1 {un}" : $"{n} {plusieurs}";

    private static IEnumerable<Pastille> Toutes(EtatBarre e)
    {
        // Ne dépendent pas du serveur : affichées même serveur injoignable
        if (e.SecondesAvantDeconnexion is > 0 and <= PreavisDeconnexionSecondes and var s)
            yield return new("deconnexion", $"Déconnexion dans {(s + 59) / 60} min", Attention, null,
                "Déconnexion automatique après une période sans activité : une touche ou un clic la repousse.");
        if (e.ErreurImprimante is { Length: > 0 } erreur)
            yield return new("imprimante", "Imprimante tickets : échec", Anormal, e.Droit("admin.parametres") ? "admin/imprimantes" : null,
                $"Dernier ticket non imprimé : {erreur}");

        if (!e.ServeurJoignable) yield break;

        // Anomalies connues du serveur
        if (e.Liaisons?.Central is { } central)
            yield return new("liaison-central", central.Libelle, NiveauLiaison(central), e.Droit("admin.parametres") ? "admin/synchronisation" : null, central.Detail);
        if (e.Liaisons?.Second is { } second)
            yield return new("liaison-second", second.Libelle, NiveauLiaison(second), e.Droit("role.titulaire") ? "admin/second-serveur" : null, second.Detail);
        if (Sauvegarde(e) is { } sauvegarde) yield return sauvegarde;
        if (e.Compteurs is { JournauxEnAnomalie: true } && e.Droit("role.titulaire"))
            yield return new("journaux", "Journaux : anomalie", Anormal, "admin/controle-interne?rubrique=integrite",
                "Intégrité des journaux : une chaîne est rompue ou n’a pas pu être vérifiée.");

        // Caisse du poste : grise, orange si elle a été ouverte un autre jour (clôture oubliée)
        if (e.Droit("caisse.vendre") && e.Poste?.CaisseOuverteLe is { } ouverte)
        {
            var o = Local(ouverte);
            yield return o.Date == e.Maintenant.Date
                ? new("caisse", $"Caisse depuis {o:HH:mm}", null, "cloture", "Relève et clôture de la caisse")
                : new("caisse", $"Caisse depuis le {o:dd/MM} à {o:HH:mm}", Attention, "cloture", "Caisse ouverte un autre jour : relève et clôture de la caisse");
        }

        // Compteurs (déjà filtrés par les droits au serveur ; revérifiés ici pour le lien et l'affichage)
        if (e.Compteurs is not { } c) yield break;
        if (c.VentesEnAttente > 0 && e.Droit("caisse.vendre"))
            yield return new("ventes-attente", Pluriel(c.VentesEnAttente, "vente en attente", "ventes en attente"), null, "caisse",
                "Ventes mises en attente sur ce poste");
        if (c.ProduitsDisponibles > 0 && e.Droit("caisse.vendre"))
            yield return new("produits-revenus", Pluriel(c.ProduitsDisponibles, "produit revenu", "produits revenus"), null, "patients/attente",
                "Produits redevenus disponibles : patients à prévenir et produits dus des ventes en attente");
        if (c.VoletsChifa > 0 && e.Droit("caisse.vendre"))
            yield return new("volets-chifa", Pluriel(c.VoletsChifa, "volet Chifa", "volets Chifa"), null, "chifa", "Ventes Chifa en attente de volet");
        if (c.ChifaADeposer > 0 && e.Droit("chifa.bordereaux"))
            yield return new("chifa-a-deposer", $"{c.ChifaADeposer} Chifa à déposer", null, "bordereaux-chifa?rubrique=a-deposer",
                "Ventes Chifa à déposer en bordereau");
        if (c.TraitesAPayer > 0 && e.Droit("achats.regler"))
            yield return new("traites", Pluriel(c.TraitesAPayer, "traite à payer", "traites à payer"), null, "achats?onglet=traites",
                "Traites échues ou à échéance sous 7 jours");
    }

    /// <summary>Seules les deux classes de la charte sont permises (toute autre valeur reçue : « attention »).</summary>
    private static string NiveauLiaison(AnomalieLiaisonDto a) => a.Niveau == Anormal ? Anormal : Attention;

    /// <summary>
    /// Sauvegarde seulement en retard ou en échec (rien quand elle est à jour) : échouée ou disque absent en rouge ; jamais faite,
    /// automatique désactivée ou dernière réussie plus vieille qu'hier en orange.
    /// </summary>
    private static Pastille? Sauvegarde(EtatBarre e)
    {
        if (e.Poste is not { } p) return null;
        var lien = e.Droit("admin.sauvegardes") ? "admin/sauvegardes" : null;
        var quand = p.DerniereSauvegarde is { } d ? $"le {Local(d):dd/MM à HH:mm}" : null;
        return p.StatutSauvegarde switch
        {
            "echouee" => new("sauvegarde", "Sauvegarde échouée", Anormal, lien, $"Dernière sauvegarde échouée {quand}"),
            "disque_absent" => new("sauvegarde", "Sauvegarde : disque absent", Anormal, lien, $"Disque de sauvegarde absent {quand}"),
            null => new("sauvegarde", p.SauvegardeActive ? "Sauvegarde jamais faite" : "Sauvegarde désactivée", Attention, lien,
                p.SauvegardeActive ? "Aucune sauvegarde n’a encore été faite" : "Sauvegarde automatique désactivée"),
            _ when !p.SauvegardeActive => new("sauvegarde", "Sauvegarde désactivée", Attention, lien,
                $"Sauvegarde automatique désactivée ; dernière sauvegarde {quand}"),
            _ when p.DerniereSauvegarde is { } r && Local(r).Date < e.Maintenant.Date.AddDays(-1) => new("sauvegarde", "Sauvegarde en retard",
                Attention, lien, $"Dernière sauvegarde réussie {quand}"),
            _ => null
        };
    }
}
