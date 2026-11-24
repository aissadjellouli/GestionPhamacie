namespace Pharma.Shared;

// ---- Bascule du 2e serveur, vague 2 (lot V2-0, 2026-11-23) : contrats figés ----
// Aucune logique ici. Description complète : docs/taches/2026-11-23-bascule-2e-serveur/contrat-api.md.
// Les « énumérations » sont des chaînes en minuscules (valeurs de fil) : constantes ci-dessous, champs typés string.

/// <summary>Rôle d'un serveur (champ « role » du healthcheck, de la consigne et de l'état inter-serveurs).</summary>
public static class RoleServeur
{
    public const string Principal = "principal";
    public const string Veille = "veille";
    public const string Ecarte = "ecarte";
}

/// <summary>Consigne donnée à un poste par le 2e serveur en réponse à son rapport.</summary>
public static class ConsigneVigie
{
    public const string Rester = "rester";
    public const string Attendre = "attendre";
    public const string Relais = "relais";
    public const string Basculer = "basculer";
}

/// <summary>Motifs du 503 « serveur_ecarte » (champ « motif »). « isole » : le poste ne bascule PAS.</summary>
public static class MotifEcartServeur
{
    public const string Bascule = "bascule";
    public const string TimelinesEgales = "timelines_egales";
    public const string Isole = "isole";
}

/// <summary>Types d'alerte transmis au serveur 1 par le 2e serveur (POST /api/inter-serveurs/alerte).</summary>
public static class TypeAlerteInterServeurs
{
    public const string ReseauPoste = "reseau_poste";
    public const string ClesJetonsDifferentes = "cles_jetons_differentes";
    public const string ApiServeur1Arretee = "api_serveur1_arretee";
    public const string EgaliteMajorite = "egalite_majorite";
    /// <summary>Lot V2-4 (ajout) : la promotion automatique n'a pas eu lieu et le titulaire doit décider (réglage coupé, droit 166b absent, lignée illisible, preuve manquante).</summary>
    public const string PromotionManuelleNecessaire = "promotion_manuelle_necessaire";
}

/// <summary>Codes stables des ProblemDetails (champ « code ») ajoutés par la vague 2.</summary>
public static class CodesBasculeVague2
{
    public const string ServeurEnVeille = "serveur_en_veille";
    public const string SignatureInvalide = "signature_invalide";
    public const string DejaEcarte = "deja_ecarte";
    public const string RetraitImpossible = "retrait_impossible";
}

/// <summary>En-têtes de la signature entre serveurs (requêtes et réponses).</summary>
public static class EnTetesInterServeurs
{
    public const string Serveur = "X-Pharma-Serveur";
    public const string Horodatage = "X-Pharma-Horodatage";
    public const string Nonce = "X-Pharma-Nonce";
    public const string Signature = "X-Pharma-Signature";
    public const string Poste = "X-Pharma-Poste";
}

/// <summary>Textes imposés par le titulaire (Q32).</summary>
public static class MessagesBasculeVague2
{
    public const string PosteIsole = "Ce poste est coupé du réseau de la pharmacie. Les ventes sont suspendues sur ce poste. Prévenez le titulaire.";
}

/// <summary>Valeurs par défaut (secondes sauf mention) des réglages SecondServeur:Bascule:* ; bornes dans le contrat.</summary>
public static class ReglagesBasculeParDefaut
{
    public const int PertePosteSecondes = 30;
    public const int InjoignableServeur1Secondes = 30;
    public const int PlancherReglableSecondes = 15;
    public const int PlafondReglableSecondes = 300;
    public const int IsolementServeur1Secondes = 20;
    public const int PromotionMinimumSecondes = 60;
    public const int RetourVoieDirecteSecondes = 120;
    public const int DemarrageVeilleSansDecisionSecondes = 120;
    public const int PosteActifMinutes = 3;
    public const int PosteVuSecondes = 45;
    public const int RapportPosteSecondes = 15;
    public const int RapportPosteApresEchecSecondes = 5;
    public const int SondeSecondes = 5;
    public const int FenetreSignatureSecondes = 60;
    public const int CacheNonceMinutes = 5;
}

/// <summary>Corps de POST /api/vigie/rapport (poste → 2e serveur).</summary>
public sealed record RapportPosteDto(string Poste, bool Serveur1Joint, DateTime? EchecDepuisUtc, string VersionPoste, string AdresseServeur1);

/// <summary>Réponse de POST /api/vigie/rapport : rôle du 2e serveur, consigne, message facultatif.</summary>
public sealed record ConsigneVigieDto(string Role, string Consigne, string? Message);

/// <summary>Réponse de GET /api/inter-serveurs/etat (signée). Timeline : null si inconnue (copie).</summary>
public sealed record EtatInterServeursDto(string Serveur, string Role, int? Timeline, bool EnRecovery, string? VersionApi, string? EmpreinteJeton, string? Reglages = null);

/// <summary>Réponse 200 de POST /api/inter-serveurs/retrait. LsnFinal au format PostgreSQL « 0/16B3748 ».</summary>
public sealed record RetraitInterServeursDto(bool Ecarte, string LsnFinal);

/// <summary>Corps de POST /api/inter-serveurs/alerte (2e serveur → serveur 1) ; Poste : seulement pour « reseau_poste ».</summary>
public sealed record AlerteInterServeursDto(string Type, string? Poste, string Message, DateTime SurvenueUtc);
