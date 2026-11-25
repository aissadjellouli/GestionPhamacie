using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Suite d'un encaissement en suspens : attendre encore, renvoyer maintenant, ou abandonner le renvoi automatique.</summary>
public enum EtapeRenvoi { Attendre, Renvoyer, Expire }

/// <summary>
/// Encaissement interrompu par la panne du serveur (Q25, corrigé le 2026-11-25 après l'essai réel) : sur un poste qui a une adresse de
/// secours, la commande est gardée telle quelle, avec sa clé d'encaissement, et renvoyée UNE fois dès que le poste a basculé sur le 2e serveur
/// (la promotion arrive 1 à 2 minutes après la panne) ou a retrouvé son serveur. Le serveur qui la reçoit rend la vente déjà arrivée par
/// réplication au lieu d'en créer une seconde. Au-delà de <see cref="DelaiMaximal"/>, le renvoi automatique est abandonné : l'employé encaisse de
/// nouveau, sans rien changer au panier. Un seul serveur : rien de tout cela (comportement inchangé).
/// </summary>
public sealed class RenvoiEncaissement(CommandeVenteDto commande, string mode, int generation, DateTime depuis)
{
    /// <summary>Attente maximale du renvoi automatique (promotion la plus lente constatée à l'essai réel : environ 2 minutes).</summary>
    public static readonly TimeSpan DelaiMaximal = TimeSpan.FromMinutes(5);

    /// <summary>Fréquence de l'examen pendant l'attente (même rythme que la vigie du poste après un échec).</summary>
    public static readonly TimeSpan Cadence = TimeSpan.FromSeconds(5);

    public const string MessageSuspens =
        "Encaissement interrompu par la panne du serveur : il sera renvoyé automatiquement dès que ce poste aura basculé sur le 2e serveur "
        + "ou retrouvé son serveur. Ne changez rien au panier : il ne sera jamais encaissé deux fois.";

    public const string MessageExpire =
        "Le serveur ne répond toujours pas : la vente est peut-être déjà enregistrée. Vérifiez les dernières ventes, "
        + "ou encaissez de nouveau sans rien changer au panier : il ne sera jamais encaissé deux fois.";

    public const string MessageAnnule =
        "Renvoi automatique annulé. Encaissez de nouveau sans rien changer au panier : il ne sera jamais encaissé deux fois.";

    public const string MessageRenvoyee = "renvoyée automatiquement après la panne du serveur";

    /// <summary>Commande envoyée la première fois (même clé d'encaissement) : c'est elle qui est renvoyée.</summary>
    public CommandeVenteDto Commande { get; } = commande;
    public string Mode { get; } = mode;
    /// <summary>Génération de bascule du poste au moment de l'échec.</summary>
    public int Generation { get; } = generation;
    public DateTime Depuis { get; } = depuis;

    /// <summary>
    /// Mise en suspens : seulement sur un poste qui a une adresse de secours, et seulement après une panne (serveur injoignable ou délai
    /// dépassé) ; tout autre refus du serveur reste affiché tel quel.
    /// </summary>
    public static bool APlacer(bool basculeActive, bool erreurDePanne) => basculeActive && erreurDePanne;

    /// <summary>
    /// Étape suivante. Renvoyer : le poste a basculé (génération changée) ou son serveur répond de nouveau, avec un employé connecté et des
    /// ventes permises (jamais sur un poste isolé). Expire : délai maximal atteint sans pouvoir renvoyer.
    /// </summary>
    public EtapeRenvoi Etape(DateTime maintenant, int generationActuelle, bool serveurJoint, bool employeConnecte, bool ventesSuspendues)
    {
        if (employeConnecte && !ventesSuspendues && (generationActuelle != Generation || serveurJoint)) return EtapeRenvoi.Renvoyer;
        return maintenant - Depuis >= DelaiMaximal ? EtapeRenvoi.Expire : EtapeRenvoi.Attendre;
    }
}
