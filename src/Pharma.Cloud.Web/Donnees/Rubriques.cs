using System.Globalization;

namespace Pharma.Cloud.Web.Donnees;

/// <summary>Sous-rubrique de la fiche d'une officine : clé d'adresse, titre, fonction de lecture (liste fixe, jamais saisie).</summary>
public sealed record Rubrique(string Cle, string Titre, string Fonction, bool Pagine, string Explication);

/// <summary>Rubriques de la fiche d'une officine (menu de gauche) et libellés des colonnes rendues par la base.</summary>
public static class Rubriques
{
    public static readonly IReadOnlyList<Rubrique> Toutes =
    [
        new("resume", "Résumé", "cloud_ecran_fiche", false, "État de l'officine vu du serveur central."),
        new("certificats", "Certificats", "cloud_ecran_certificats", false, "Certificats des serveurs de l'officine (début d'empreinte seulement)."),
        new("codes", "Codes d'activation", "cloud_ecran_codes_activation", false, "Codes émis pour l'officine : dates seulement, jamais le code."),
        new("chargements", "Chargements", "cloud_ecran_chargements", false, "20 derniers chargements complets."),
        new("lots", "Lots reçus", "cloud_ecran_lots", false, "50 derniers lots reçus, sans leur contenu."),
        new("epoques", "Époques", "cloud_ecran_epoques", false, "Dernier lot reçu par époque."),
        new("changements", "Changements d'époque", "cloud_ecran_changements_epoque", false, "Changements d'époque déclarés par l'officine (restauration)."),
        new("volumes", "Volumes", "cloud_ecran_volumes", false, "Octets reçus par jour (31 derniers jours)."),
        new("integrite", "Intégrité : chaînes", "cloud_ecran_integrite_chaines", false, "État de la vérification de chaque chaîne."),
        new("anomalies", "Intégrité : anomalies", "cloud_ecran_anomalies", true, "Nature et rang de chaque anomalie, sans détail."),
        new("restaurations", "Restaurations", "cloud_ecran_restaurations", false, "Restaurations constatées par la vérification d'intégrité."),
        new("ordres", "Ordres", "cloud_ecran_ordres", true, "Ordres envoyés à l'officine : type et statut, jamais leurs données."),
        new("journal", "Journal de sécurité", "cloud_ecran_journal_securite", true, "Événements de sécurité de l'officine."),
    ];

    public static Rubrique Trouver(string? cle) => Toutes.FirstOrDefault(r => r.Cle == cle) ?? Toutes[0];

    private static readonly Dictionary<string, string> Libelles = new()
    {
        ["libelle"] = "Officine", ["etat"] = "État", ["cree_le"] = "Créée le", ["resiliee_le"] = "Partie le", ["dernier_contact"] = "Dernier contact",
        ["en_retard"] = "En retard", ["dernier_lot"] = "Dernier lot", ["derniere_epoque"] = "Époque", ["chargement_en_cours"] = "Chargement en cours",
        ["echeance_certificat"] = "Échéance de certificat", ["anomalies"] = "Anomalies", ["serveur"] = "Serveur", ["empreinte_courte"] = "Empreinte",
        ["emis_le"] = "Émis le", ["echeance"] = "Échéance", ["revoque_le"] = "Révoqué le", ["remplace_courte"] = "Remplace",
        ["expire_le"] = "Expire le", ["utilise_le"] = "Utilisé le", ["certificat_courte"] = "Certificat", ["numero"] = "N°",
        ["motif_chargement"] = "Motif", ["epoque"] = "Époque", ["debut_le"] = "Début", ["fin_le"] = "Fin", ["lots_abandonnes"] = "Lots abandonnés",
        ["octets_recus"] = "Octets reçus", ["absentes"] = "Lignes absentes", ["lignes_remplacees"] = "Lignes remplacées", ["nb_lignes"] = "Lignes",
        ["octets"] = "Octets", ["version_contrat"] = "Contrat", ["recu_le"] = "Reçu le", ["expurge_le"] = "Expurgé le", ["dernier_recu_le"] = "Dernier reçu le",
        ["ancienne_epoque"] = "Ancienne époque", ["nouvelle_epoque"] = "Nouvelle époque", ["dernier_lot_local"] = "Dernier lot (officine)",
        ["dernier_lot_central"] = "Dernier lot (central)", ["detecte_le"] = "Détecté le", ["decide_le"] = "Décidé le",
        ["avant_premier_lot"] = "Avant le premier lot", ["jour"] = "Jour", ["journal"] = "Journal", ["cle_chaine"] = "Chaîne", ["statut"] = "Statut",
        ["dernier_rang"] = "Dernier rang", ["rang_rupture"] = "Rang de rupture", ["non_verifiables"] = "Non vérifiables", ["verifie_le"] = "Vérifié le",
        ["verification_complete_le"] = "Vérification complète", ["rang"] = "Rang", ["nature"] = "Nature", ["detectee_le"] = "Détectée le",
        ["classe"] = "Classement", ["rang_commun"] = "Rang commun", ["lignes_ancienne"] = "Lignes perdues", ["sceaux_perdus"] = "Sceaux perdus",
        ["premiere_date"] = "Première date", ["derniere_date"] = "Dernière date", ["constatee_le"] = "Constatée le", ["type"] = "Type",
        ["statut_le"] = "Statut le", ["le"] = "Le", ["evenement"] = "Événement", ["detail"] = "Détail", ["resultat"] = "Résultat",
        ["objet"] = "Objet", ["officine"] = "Officine",
        // Titulaire (script central 032)
        ["rattachee_le"] = "Rattachée le", ["droits"] = "Droits", ["acteur"] = "Par",
    };

    /// <summary>Lot S7.3 : ordres que le titulaire peut envoyer (liste fermée, même liste que cloud_ordre_titulaire_permis, script 034).</summary>
    public static readonly IReadOnlyList<(string Type, string Libelle, string Explication)> OrdresTitulaire =
    [
        ("essai", "Essai de liaison", "Vérifie que l'officine relève les ordres ; rien ne change dans l'officine."),
        ("sauvegarde", "Demander une sauvegarde", "Le titulaire confirme sur le poste de l'officine, au moment choisi, avant que la sauvegarde parte."),
        ("verification_integrite", "Demander une vérification d'intégrité", "Les journaux chaînés de l'officine sont revérifiés (lecture seule) ; une rupture vous est signalée."),
    ];

    /// <summary>Statuts d'un ordre affichés au titulaire.</summary>
    public static readonly Dictionary<string, string> StatutsOrdre = new()
    {
        ["en_attente"] = "En attente", ["a_confirmer"] = "À confirmer sur le poste", ["applique"] = "Appliqué", ["refuse"] = "Refusé",
        ["expire"] = "Expiré", ["annule"] = "Annulé",
    };

    /// <summary>Lot S7.4 : statut d'une proposition de réglages communs, vu du titulaire au central.</summary>
    public static readonly Dictionary<string, string> StatutsReglages = new()
    {
        ["en_attente"] = "Proposé", ["a_confirmer"] = "Proposé (sur le poste)", ["applique"] = "Accepté", ["refuse"] = "Refusé",
        ["expire"] = "Expiré", ["annule"] = "Annulé",
    };

    /// <summary>Colonnes jamais affichées (techniques internes : clés, total).</summary>
    public static readonly HashSet<string> Masquees = ["total", "id", "officine_id"];

    public static string Libelle(string colonne) => Libelles.TryGetValue(colonne, out var l) ? l : colonne;

    public static readonly Dictionary<string, string> Etats = new()
    {
        ["active"] = "Active", ["en_attente"] = "En attente d'activation", ["sans_certificat_valable"] = "Sans certificat valable", ["partie"] = "Partie",
    };

    /// <summary>Valeur affichée : dates en heure locale du serveur des écrans, booléens oui / non, nombres groupés.</summary>
    public static string Afficher(string colonne, object? valeur) => valeur switch
    {
        null => "—",
        DateTime d => (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d).ToString(colonne == "jour" ? "dd/MM/yyyy" : "dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("fr-FR")),
        DateOnly j => j.ToString("dd/MM/yyyy", CultureInfo.GetCultureInfo("fr-FR")),
        bool b => b ? "oui" : "non",
        long or int or short => string.Format(CultureInfo.GetCultureInfo("fr-FR"), "{0:N0}", valeur),
        Guid g => g.ToString()[..8],
        string s when colonne == "etat" && Etats.TryGetValue(s, out var e) => e,
        string s when colonne == "droits" => string.Join(", ", s.Split(',').Select(d => d switch { "consulter" => "tableau de bord", "journal_acces" => "journal des accès", "ordres" => "ordres à distance", _ => d })),
        string s when colonne == "type" && OrdresTitulaire.FirstOrDefault(o => o.Type == s) is { Libelle: { } l } => l,
        string s when colonne == "type" && s == Pharma.Domain.Reglages.ReglagesCommuns.TypeOrdre => "Réglages communs",
        string s when colonne == "statut" && StatutsOrdre.TryGetValue(s, out var so) => so,
        string s when colonne == "acteur" => s switch { "vous" => "Vous", "editeur" => "Éditeur (support)", "titulaire" => "Autre titulaire", "poste" => "Poste de l’officine", "console" => "Console du central", _ => s },
        _ => Convert.ToString(valeur, CultureInfo.GetCultureInfo("fr-FR")) ?? "—",
    };
}

/// <summary>Valeurs du tableau de bord du titulaire : NULL de la base = donnée non montée au central, affichée « non disponible » (jamais 0).</summary>
public static class Tableau
{
    public const string NonDisponible = "non disponible";

    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Montant(object? valeur) => valeur switch
    {
        decimal d => d.ToString("N2", Fr) + " DA",
        null => NonDisponible,
        _ => Convert.ToString(valeur, Fr) ?? NonDisponible,
    };

    public static string Nombre(object? valeur) => valeur switch
    {
        int or long or short => string.Format(Fr, "{0:N0}", valeur),
        null => NonDisponible,
        _ => Convert.ToString(valeur, Fr) ?? NonDisponible,
    };
}
