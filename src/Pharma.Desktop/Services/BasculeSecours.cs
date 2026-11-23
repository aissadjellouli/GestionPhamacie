using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Ce que le poste fait d'un refus « serveur écarté » ou d'une consigne du 2e serveur.</summary>
public enum ActionBascule { Aucune, Attendre, Relais, Basculer, Indecis, Isole }

/// <summary>
/// Décisions pures de la bascule du poste (lot V2-6), sans réseau ni fichier : testées seules.
/// Officine à un seul serveur (pas d'adresse de secours) : rien n'est décidé, le poste se comporte comme avant.
/// </summary>
public static class BasculeSecours
{
    public const string MessageAttente = "Le serveur principal ne répond plus. Vérification avec le 2e serveur en cours : patientez. Votre vente en cours est conservée, ne fermez pas Tracodex Officine.";
    public const string MessageBascule = "Ce poste est passé sur le 2e serveur, devenu serveur principal. Votre session et votre vente en cours sont conservées.";
    public const string MessageRelais = "Ce poste ne joint plus le serveur principal : il passe par le 2e serveur. Le travail continue ; le titulaire est prévenu.";
    public const string MessageIndecis = "Les deux serveurs sont bloqués en lecture seule : le titulaire doit choisir. Aucune opération possible pour l'instant ; votre vente reste conservée tant que Tracodex Officine reste ouvert.";
    public const string MessageRepli = "Ce poste est passé sur le 2e serveur. Reconnectez-vous : votre vente en cours sera reprise.";
    public const string MessageIsole = MessagesBasculeVague2.PosteIsole;
    public const string MessageReglagesNonEnregistres = " Attention : les réglages du poste n'ont pas pu être enregistrés ; au prochain démarrage, ce poste reviendra à l'ancienne adresse.";
    public const string MessageSecoursPasPrincipal = "Le 2e serveur répond mais n'est pas devenu serveur principal (il est en veille) : le poste n'y est pas dirigé. Attendez la fin de la bascule du serveur ou prévenez le titulaire.";

    /// <summary>Une adresse de secours est réglée : la vigie, les rapports et la bascule existent.</summary>
    public static bool SecoursConfigure(ReglagesPoste.Valeurs v) => AdresseApiCourante.Lire(v.AdresseApiSecours) is not null;

    /// <summary>Case « Basculer automatiquement » : cochée par défaut dès qu'une adresse de secours existe.</summary>
    public static bool Automatique(ReglagesPoste.Valeurs v) => SecoursConfigure(v) && (v.BasculeAutomatique ?? true);

    /// <summary>Rôle annoncé par le healthcheck : seul « principal » (ou l'absence de rôle d'un serveur plus ancien) autorise d'y aller.</summary>
    public static bool EstPrincipal(string? role) => role is null || role == RoleServeur.Principal;

    /// <summary>Réponse 503 « serveur_ecarte » : bascule (motif « bascule »), indécis (« timelines_egales » ou motif inconnu), isolé (jamais de bascule).</summary>
    public static ActionBascule PourEcart(string? motif, bool automatique) => motif switch
    {
        MotifEcartServeur.Bascule => automatique ? ActionBascule.Basculer : ActionBascule.Aucune,
        MotifEcartServeur.Isole => ActionBascule.Isole,
        _ => ActionBascule.Indecis
    };

    /// <summary>Consigne du 2e serveur : sans la case cochée, aucune action automatique (les rapports partent quand même).</summary>
    public static ActionBascule PourConsigne(string? consigne, bool automatique)
    {
        if (!automatique) return ActionBascule.Aucune;
        return consigne switch
        {
            ConsigneVigie.Attendre => ActionBascule.Attendre,
            ConsigneVigie.Relais => ActionBascule.Relais,
            ConsigneVigie.Basculer => ActionBascule.Basculer,
            _ => ActionBascule.Aucune
        };
    }

    /// <summary>Après la bascule (Q27) : l'adresse de secours devient l'adresse de l'API, l'ancienne adresse devient le secours. Les autres réglages sont gardés.</summary>
    public static ReglagesPoste.Valeurs Echanger(ReglagesPoste.Valeurs v, string adresseServeur1Effective)
    {
        var copie = System.Text.Json.JsonSerializer.Deserialize<ReglagesPoste.Valeurs>(System.Text.Json.JsonSerializer.Serialize(v))!;
        copie.AdresseApi = v.AdresseApiSecours;
        copie.AdresseApiSecours = adresseServeur1Effective;
        return copie;
    }
}
