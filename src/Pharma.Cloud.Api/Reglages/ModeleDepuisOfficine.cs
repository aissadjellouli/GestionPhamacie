using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pharma.Domain.Reglages;
using Pharma.Shared;

namespace Pharma.Cloud.Api.Reglages;

/// <summary>Ligne d'une table miroir : nom de colonne, valeur lue (null si vide).</summary>
public sealed class LigneMiroir(IReadOnlyDictionary<string, object?> valeurs)
{
    public object? this[string colonne] => valeurs.TryGetValue(colonne, out var v) ? v : null;
}

/// <summary>
/// Fabrique un modèle mutu-v1 à partir des valeurs réelles d'une officine (lignes de la table miroir reçues au central).
/// Aucun identifiant propre à l'officine (id, produit, auteur local, horaires d'ouverture) ; aucune publication.
/// </summary>
public static class ModeleDepuisOfficine
{
    private enum Genre { Entier, Booleen, Texte, TexteOuNull, Montant }

    private static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Indente = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, (string Colonne, Genre Genre)[]> LigneUnique = new(StringComparer.Ordinal)
    {
        [TablesMutualisables.Fiscal] = [("conservation_annees", Genre.Entier), ("format_export_dgi", Genre.Texte), ("separateur_csv", Genre.Texte), ("fuseau", Genre.Texte)],
        [TablesMutualisables.Tracabilite] = [("boite_obligatoire", Genre.Booleen), ("patient_obligatoire", Genre.Texte)],
        [TablesMutualisables.Serialisation] = [("actif", Genre.Booleen), ("factice_hors_ligne", Genre.Booleen)],
        [TablesMutualisables.Substitution] = [("equivalent_exact_seulement", Genre.Booleen)],
        [TablesMutualisables.FormulaireSms] = [("texte", Genre.TexteOuNull)],
        [TablesMutualisables.Inventaire] = [("seuil_boites", Genre.Entier), ("seuil_montant", Genre.Montant)],
        [TablesMutualisables.Alertes] =
        [
            ("jours_orange", Genre.Entier), ("jours_rouge", Genre.Entier), ("horizon_risque_jours", Genre.Entier), ("delai_fournisseur_jours", Genre.Entier),
            ("jours_securite", Genre.Entier), ("jours_cible_commande", Genre.Entier), ("jours_dormant", Genre.Entier),
            ("seuil_lot_delaisse", Genre.Entier), ("seuil_lot_delaisse_collectif", Genre.Entier)
        ],
        [TablesMutualisables.Surveillance] =
        [
            ("seuil_lignes_retirees", Genre.Entier), ("seuil_tiroir", Genre.Entier), ("seuil_attentes_abandonnees", Genre.Entier), ("seuil_remises", Genre.Entier),
            ("seuil_remises_patient", Genre.Entier), ("seuil_ajustements", Genre.Entier), ("seuil_casses", Genre.Entier), ("seuil_echantillons", Genre.Entier),
            ("jours_comptage_sensibles", Genre.Entier), ("reception_aveugle", Genre.Booleen), ("jours_avoir_retour", Genre.Entier),
            ("seuil_credit_nouveau_patient", Genre.Montant), ("jours_patient_recent", Genre.Entier), ("seuil_credits_jour", Genre.Montant),
            ("jours_credit_sans_remboursement", Genre.Entier), ("seuil_autorisations_valideur", Genre.Entier), ("seuil_autorisations_employe", Genre.Entier),
            ("minutes_postes_simultanes", Genre.Entier), ("seuil_changements_poste", Genre.Entier), ("seuil_baisses_prix", Genre.Entier), ("seuil_sorties_tiroir", Genre.Entier)
        ]
    };

    /// <summary>Colonnes du miroir à lire pour une table.</summary>
    public static IReadOnlyList<string> ColonnesALire(string table) => table switch
    {
        TablesMutualisables.AlertesPsychotropes => ["code", "active", "seuil", "periode_jours", "exiger_valideur", "unites_par_jour"],
        TablesMutualisables.Motifs => ["liste", "code", "libelle", "actif", "ordre", "effet", "detail"],
        TablesMutualisables.Remises => ["id", "produit_id", "categorie", "pourcentage", "date_debut", "date_fin", "motif", "arretee_le", "modele_cle"],
        _ => LigneUnique.TryGetValue(table, out var c) ? c.Select(x => x.Colonne).ToArray() : throw new ArgumentException($"Table « {table} » non mutualisable.")
    };

    /// <summary>Ordre de lecture (déterministe) des lignes du miroir.</summary>
    public static string Ordre(string table) => table switch
    {
        TablesMutualisables.AlertesPsychotropes => "code",
        TablesMutualisables.Motifs => "liste, code",
        _ => "id"
    };

    /// <summary>
    /// Fichier mutu-v1 (indenté) avec la valeur construite, et les notes (ce qui a été écarté). L'enveloppe est un point de départ :
    /// version, date et auteur sont remplacés par le central à la publication.
    /// </summary>
    public static (string Json, IReadOnlyList<string> Notes) Construire(string table, Guid officine, IReadOnlyList<LigneMiroir> lignes, string mode, DateOnly aujourdhui, DateTime maintenantUtc)
    {
        var notes = new List<string>();
        JsonObject valeur = table switch
        {
            TablesMutualisables.AlertesPsychotropes => Psychotropes(lignes),
            TablesMutualisables.Motifs => Motifs(lignes),
            TablesMutualisables.Remises => Remises(officine, lignes, aujourdhui, notes),
            _ => UneLigne(table, lignes)
        };
        var racine = new JsonObject
        {
            ["format"] = ContratMutu.Format,
            ["mode"] = mode,
            ["version_modele"] = 1,
            ["publie_le"] = maintenantUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["auteur"] = "a-completer",
            ["valeur"] = valeur
        };
        if (ValidationModele.PorteAjoutsLocaux(table)) racine["ajouts_locaux"] = true;
        // Forme canonique du format (clés triées, nombres sans zéro final), puis mise en page lisible
        var brut = racine.ToJsonString(Compact);
        string canonique;
        try
        {
            var element = SerialisationMutuV1.Lire(table, Encoding.UTF8.GetBytes(brut));
            canonique = Encoding.UTF8.GetString(SerialisationMutuV1.Serialiser(table, element));
        }
        catch (FormatMutuException e)
        {
            notes.Add($"Mise en forme du format impossible (le fichier est écrit tel quel) : {e.Message}");
            canonique = brut;
        }
        return (JsonNode.Parse(canonique)!.ToJsonString(Indente), notes);
    }

    private static InvalidOperationException Vide() =>
        new("L'officine n'a encore rien envoyé pour cette table (miroir vide) : pas de valeur à reprendre.");

    private static JsonObject UneLigne(string table, IReadOnlyList<LigneMiroir> lignes)
    {
        if (lignes.Count == 0) throw Vide();
        var l = lignes[0];
        var o = new JsonObject();
        foreach (var (colonne, genre) in LigneUnique[table]) o[colonne] = Convertir(l[colonne], genre, colonne);
        return o;
    }

    private static JsonObject Psychotropes(IReadOnlyList<LigneMiroir> lignes)
    {
        if (lignes.Count == 0) throw Vide();
        var regles = new JsonArray();
        foreach (var l in lignes)
            regles.Add(new JsonObject
            {
                ["code"] = Convertir(l["code"], Genre.Texte, "code"),
                ["active"] = Convertir(l["active"], Genre.Booleen, "active"),
                ["seuil"] = Convertir(l["seuil"], Genre.Entier, "seuil"),
                ["periode_jours"] = l["periode_jours"] is null ? null : Convertir(l["periode_jours"], Genre.Entier, "periode_jours"),
                ["exiger_valideur"] = Convertir(l["exiger_valideur"], Genre.Booleen, "exiger_valideur"),
                ["unites_par_jour"] = l["unites_par_jour"] is null ? null : Convertir(l["unites_par_jour"], Genre.Entier, "unites_par_jour")
            });
        return new JsonObject { ["regles"] = regles };
    }

    private static JsonObject Motifs(IReadOnlyList<LigneMiroir> lignes)
    {
        if (lignes.Count == 0) throw Vide();
        var motifs = new JsonArray();
        foreach (var l in lignes)
            motifs.Add(new JsonObject
            {
                ["liste"] = Convertir(l["liste"], Genre.Texte, "liste"),
                ["code"] = Convertir(l["code"], Genre.Texte, "code"),
                ["libelle"] = Convertir(l["libelle"], Genre.Texte, "libelle"),
                ["actif"] = Convertir(l["actif"], Genre.Booleen, "actif"),
                ["ordre"] = Convertir(l["ordre"], Genre.Entier, "ordre"),
                ["effet"] = l["effet"] is string { Length: > 0 } e ? e : "aucun",
                ["detail"] = l["detail"] is string { Length: > 0 } d ? d : "aucun"
            });
        return new JsonObject { ["motifs"] = motifs };
    }

    private static JsonObject Remises(Guid officine, IReadOnlyList<LigneMiroir> lignes, DateOnly aujourdhui, List<string> notes)
    {
        var remises = new JsonArray();
        int produit = 0, arretees = 0, terminees = 0;
        foreach (var l in lignes)
        {
            if (l["arretee_le"] is not null) { arretees++; continue; }
            if (l["produit_id"] is not null || l["categorie"] is not string { Length: > 0 }) { produit++; continue; }
            var fin = Date(l["date_fin"], "date_fin");
            if (fin < aujourdhui) { terminees++; continue; }
            var cle = l["modele_cle"] is string { Length: > 0 } c ? c : CleStable(officine, l["id"]);
            remises.Add(new JsonObject
            {
                ["cle"] = cle,
                ["categorie"] = ((string)l["categorie"]!).Trim(),
                ["pourcentage"] = Convertir(l["pourcentage"], Genre.Montant, "pourcentage"),
                ["date_debut"] = Date(l["date_debut"], "date_debut").ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["date_fin"] = fin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["motif"] = l["motif"] is string m && m.Trim().Length > 0 ? m.Trim() : null
            });
        }
        if (produit > 0) notes.Add($"{produit} remise(s) sur un produit ignorée(s) : le modèle ne porte que des catégories (D6).");
        if (arretees > 0) notes.Add($"{arretees} remise(s) arrêtée(s) ignorée(s).");
        if (terminees > 0) notes.Add($"{terminees} remise(s) déjà terminée(s) ignorée(s).");
        return new JsonObject { ["remises"] = remises };
    }

    /// <summary>Identifiant stable « c_ » + 12 caractères, dérivé de l'officine et de la remise : même fichier si on refait le modèle.</summary>
    private static string CleStable(Guid officine, object? id) =>
        "c_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{officine}:{id}")))[..12].ToLowerInvariant();

    private static DateOnly Date(object? v, string colonne) => v switch
    {
        DateOnly d => d,
        DateTime t => DateOnly.FromDateTime(t),
        _ => throw new InvalidOperationException($"Colonne « {colonne} » vide dans le miroir de l'officine.")
    };

    private static JsonNode? Convertir(object? v, Genre genre, string colonne)
    {
        if (v is null)
        {
            if (genre == Genre.TexteOuNull) return null;
            throw new InvalidOperationException($"Colonne « {colonne} » vide dans le miroir de l'officine : valeur non reçue.");
        }
        return genre switch
        {
            Genre.Entier => JsonValue.Create(Convert.ToInt32(v, CultureInfo.InvariantCulture)),
            Genre.Booleen => JsonValue.Create(Convert.ToBoolean(v, CultureInfo.InvariantCulture)),
            Genre.Montant => JsonValue.Create(Convert.ToDecimal(v, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture))
        };
    }
}
