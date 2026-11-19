using System.Globalization;
using System.Text.Json;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S4 : vérification d'intégrité faite par le serveur central (docs/conception-synchronisation.md § 22) ----
// Le serveur central vérifie les journaux chaînés reçus, les sceaux, la continuité des tickets et la fin de chaîne constatée ici ;
// son résumé redescend par la voie descendante (reglage_central, clé « integrite_centrale », format integrite-v1). Lecture seule :
// rien ici ne corrige une donnée ; une nouvelle anomalie est signalée au titulaire (une alerte par nouvelle anomalie).

/// <summary>Une chaîne vérifiée au central : mode « complet » (empreintes recalculées) ou « liaison » (rangs et chaînage seulement).</summary>
public sealed record ChaineIntegriteCentrale(string Journal, string Cle, string Mode, string Statut, long DernierRang, long NonVerifiables);

/// <summary>Une anomalie constatée au central (les 20 plus récentes sont transmises).</summary>
public sealed record AnomalieIntegriteCentrale(long Id, string Nature, string Journal, long? Rang, string? Ligne, string? Detail, DateTime? DetecteeLeUtc,
    bool Classee = false);

/// <summary>
/// Lot S6.5 (script central 023) : restauration constatée au central (deux suites du même journal après un changement d'époque
/// déclaré). L'ancienne suite est gardée au central comme preuve ; Classe : « restauration_constatee » ou « restauration_a_justifier »
/// (ligne scellée perdue, restaurations trop fréquentes) ; PriseActe : le titulaire a noté la cause (integrite_prise_acte, script 151).
/// TicketsReattribues (script central 025) : n° de tickets de l'ancienne suite repris par des ventes faites avant la détection.
/// </summary>
public sealed record RestaurationCentrale(long Id, string Journal, int AncienneEpoque, int NouvelleEpoque, long RangCommun, long? FinAncienneRang,
    long Lignes, DateTime? DuUtc, DateTime? AuUtc, decimal? MontantTtc, long SceauxPerdus, string Classe, bool PriseActe,
    IReadOnlyList<string>? TicketsReattribues = null)
{
    /// <summary>Plafond des n° lus dans le résumé (réponse d'un tiers : bornée).</summary>
    public const int MaxTicketsReattribues = 1000;

    public const string ClasseConstatee = "restauration_constatee";
    public const string ClasseAJustifier = "restauration_a_justifier";
    public bool AJustifier => Classe == ClasseAJustifier;
}

/// <summary>Résumé de la dernière vérification du serveur central (statut : ok, attente ou anomalie).</summary>
public sealed record IntegriteCentrale(string Statut, DateTime? VerifieLeUtc, DateTime? CompleteLeUtc, long AnomaliesTotal, long DerniereAnomalieId,
    long SceauxOk, long SceauxAttente, long SceauxIncoherents, long? TicketsVerifiesJusquA,
    IReadOnlyList<ChaineIntegriteCentrale> Chaines, IReadOnlyList<AnomalieIntegriteCentrale> Anomalies,
    IReadOnlyList<RestaurationCentrale>? Restaurations = null, long AnomaliesClassees = 0)
{
    public const string Cle = "integrite_centrale";
    public const string Format = "integrite-v1";
    public const string StatutOk = "ok";
    public const string StatutAttente = "attente";
    public const string StatutAnomalie = "anomalie";
    /// <summary>Lot S6.5 (S6.5-D4) : restauration constatée dont le titulaire n'a pas encore pris acte (aucune autre anomalie).</summary>
    public const string StatutRestauration = "restauration";

    /// <summary>Restaurations constatées (vide pour un serveur central antérieur au script 023).</summary>
    public IReadOnlyList<RestaurationCentrale> RestaurationsListe => Restaurations ?? [];

    /// <summary>Libellé d'une nature d'anomalie (écran et alerte au titulaire).</summary>
    public static string LibelleNature(string nature) => nature switch
    {
        "modification_refusee" => "Ligne renvoyée avec un autre contenu (refusée, l’original est gardé)",
        "suppression_refusee" => "Suppression d’une ligne de journal reçue (refusée)",
        "ligne_manquante" => "Ligne de journal jamais reçue",
        "algorithme_inconnu" => "Empreinte de longueur inconnue",
        "chainage_rompu" => "Chaînage rompu",
        "ligne_modifiee" => "Ligne modifiée (empreinte différente)",
        "sceau_sans_ligne" => "Sceau sans la ligne scellée",
        "sceau_incoherent" => "Sceau incohérent avec la ligne scellée",
        "ticket_manquant" => "Numéro de ticket manquant",
        "ticket_hors_compteur" => "Ticket au-delà du compteur",
        "vente_sans_ligne" => "Vente sans ligne au journal fiscal",
        "annulation_sans_ligne" => "Annulation sans ligne au journal fiscal",
        "reecriture_apres_envoi" => "Ligne réécrite dans l’officine après son envoi",
        "chaine_officine_plus_courte" => "Lignes déjà envoyées absentes de l’officine",
        "remplacee_apres_restauration" => "Lignes remplacées après une restauration (gardées au serveur central comme preuve)",
        "sceau_perdu_restauration" => "Ligne scellée perdue par une restauration",
        "ticket_reattribue_restauration" => "N° de ticket réattribué après une restauration",
        _ => nature
    };

    /// <summary>Lit le résumé (JSON integrite-v1) ; null si absent, d'un autre format ou illisible (jamais d'exception).</summary>
    public static IntegriteCentrale? Lire(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Texte(r, "format") != Format) return null;
            var chaines = new List<ChaineIntegriteCentrale>();
            if (r.TryGetProperty("chaines", out var cs) && cs.ValueKind == JsonValueKind.Array)
                foreach (var c in cs.EnumerateArray())
                    chaines.Add(new ChaineIntegriteCentrale(Texte(c, "journal") ?? "?", Texte(c, "cle") ?? "?", Texte(c, "mode") ?? "?",
                        Texte(c, "statut") ?? "?", Nombre(c, "dernier_rang") ?? 0, Nombre(c, "non_verifiables") ?? 0));
            var anomalies = new List<AnomalieIntegriteCentrale>();
            if (r.TryGetProperty("anomalies", out var an) && an.ValueKind == JsonValueKind.Array)
                foreach (var a in an.EnumerateArray())
                    anomalies.Add(new AnomalieIntegriteCentrale(Nombre(a, "id") ?? 0, Texte(a, "nature") ?? "?", Texte(a, "journal") ?? "?",
                        Nombre(a, "rang"), Texte(a, "ligne"), Texte(a, "detail"), Date(a, "detectee_le"), Booleen(a, "classee")));
            var restaurations = new List<RestaurationCentrale>();
            if (r.TryGetProperty("restaurations", out var rs) && rs.ValueKind == JsonValueKind.Array)
                foreach (var b in rs.EnumerateArray())
                    restaurations.Add(new RestaurationCentrale(Nombre(b, "id") ?? 0, Texte(b, "journal") ?? "?", (int)(Nombre(b, "ancienne_epoque") ?? 0),
                        (int)(Nombre(b, "nouvelle_epoque") ?? 0), Nombre(b, "rang_commun") ?? 0, Nombre(b, "fin_ancienne_rang"), Nombre(b, "lignes") ?? 0,
                        Date(b, "du"), Date(b, "au"), Decimal(b, "montant_ttc"), Nombre(b, "sceaux_perdus") ?? 0,
                        Texte(b, "classe") ?? RestaurationCentrale.ClasseAJustifier, Booleen(b, "prise_acte"), Textes(b, "tickets_reattribues")));
            var sceaux = r.TryGetProperty("sceaux", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
            return new IntegriteCentrale(Texte(r, "statut") ?? StatutAttente, Date(r, "verifie_le"), Date(r, "complete_le"),
                Nombre(r, "anomalies_total") ?? 0, Nombre(r, "derniere_anomalie_id") ?? 0,
                sceaux.ValueKind == JsonValueKind.Object ? Nombre(sceaux, "ok") ?? 0 : 0,
                sceaux.ValueKind == JsonValueKind.Object ? Nombre(sceaux, "attente") ?? 0 : 0,
                sceaux.ValueKind == JsonValueKind.Object ? Nombre(sceaux, "incoherents") ?? 0 : 0,
                Nombre(r, "tickets_verifies_jusqu_a"), chaines, anomalies, restaurations, Nombre(r, "anomalies_classees") ?? 0);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Texte(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>Liste de textes courts (n° de tickets), bornée ; absente : vide.</summary>
    private static IReadOnlyList<string> Textes(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => x.Length is > 0 and <= 40)
                .Take(RestaurationCentrale.MaxTicketsReattribues).ToList()
            : [];

    private static long? Nombre(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static bool Booleen(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.True;

    private static decimal? Decimal(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d) ? d : null;

    private static DateTime? Date(JsonElement e, string nom) =>
        Texte(e, nom) is { } t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d)
            ? d.UtcDateTime : null;
}
