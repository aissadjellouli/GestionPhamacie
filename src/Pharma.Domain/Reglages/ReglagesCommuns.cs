using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Pharma.Domain.Reglages;

/// <summary>Un réglage de la liste fermée des réglages communs : clé (colonne de parametre_alerte), libellé, bornes entières.</summary>
public sealed record ReglageCommun(string Cle, string Libelle, int Minimum, int Maximum, string Unite);

/// <summary>
/// Lot S7.4 : réglages communs préparés au serveur central par le titulaire de plusieurs officines et proposés à chacune
/// (ordre « reglages » du titulaire, format reglages-v1 ; docs/conception-synchronisation.md § 32.4). LISTE FERMÉE : seuils
/// d'alerte de stock et de péremption du réglage général (table parametre_alerte) ; jamais un prix, une TVA, un droit, un compte,
/// rien de fiscal ni de nominatif. Mêmes bornes que la base centrale (cloud_reglage_commun_bornes, script central 035) ; le poste
/// revérifie la liste et les bornes à la réception, puis les règles de la saisie locale (RegleSeuils) à l'acceptation.
/// </summary>
public static class ReglagesCommuns
{
    public const string Format = "reglages-v1";
    /// <summary>Type d'ordre qui porte un jeu de réglages communs (central et officine).</summary>
    public const string TypeOrdre = "reglages";

    public static readonly IReadOnlyList<ReglageCommun> Liste =
    [
        new("jours_orange", "Péremption : alerte orange", 1, 1095, "jours"),
        new("jours_rouge", "Péremption : alerte rouge", 1, 1095, "jours"),
        new("horizon_risque_jours", "Calcul du risque d’écoulement", 1, 1095, "jours"),
        new("delai_fournisseur_jours", "Délai de livraison", 0, 90, "jours"),
        new("jours_securite", "Stock de sécurité", 0, 180, "jours"),
        new("jours_cible_commande", "Stock visé après commande", 1, 365, "jours"),
        new("jours_dormant", "Produit dormant après", 1, 1095, "jours sans vente"),
        new("seuil_lot_delaisse", "Ancien lot délaissé : un même vendeur", 1, 1000, "boîtes"),
        new("seuil_lot_delaisse_collectif", "Ancien lot délaissé : plusieurs vendeurs", 1, 1000, "boîtes"),
    ];

    /// <summary>Taille maximale des données d'un ordre de réglages (octets UTF-8), comme la base centrale.</summary>
    public const int TailleMax = 2000;

    public static ReglageCommun? Trouver(string? cle) => Liste.FirstOrDefault(r => r.Cle == cle);

    /// <summary>
    /// Contrôle d'un jeu saisi (écrans du central) : au moins un réglage, clés de la liste fermée, bornes, rouge plus court que l'orange
    /// quand les deux sont donnés. Liste vide = valide. Code court (format, vide, hors_liste, hors_bornes, incoherent) = celui de la base.
    /// </summary>
    public static (string? Code, IReadOnlyList<string> Erreurs) Controler(IReadOnlyDictionary<string, int> valeurs)
    {
        var erreurs = new List<string>();
        if (valeurs.Count == 0) return ("vide", ["Choisissez au moins un réglage à proposer."]);
        var inconnues = valeurs.Keys.Where(k => Trouver(k) is null).ToList();
        if (inconnues.Count > 0)
            return ("hors_liste", [$"Réglage hors de la liste permise : {string.Join(", ", inconnues.Take(3))}."]);
        foreach (var (cle, v) in valeurs)
        {
            var r = Trouver(cle)!;
            if (v < r.Minimum || v > r.Maximum) erreurs.Add($"{r.Libelle} : de {r.Minimum} à {r.Maximum} {r.Unite}.");
        }
        if (erreurs.Count > 0) return ("hors_bornes", erreurs);
        if (valeurs.TryGetValue("jours_orange", out var o) && valeurs.TryGetValue("jours_rouge", out var rg) && rg >= o)
            return ("incoherent", [$"L’alerte rouge ({rg} j) doit être plus courte que l’orange ({o} j)."]);
        return (null, []);
    }

    /// <summary>Données de l'ordre : {"format":"reglages-v1","reglages":{…}} dans l'ordre de la liste fermée.</summary>
    public static string Json(IReadOnlyDictionary<string, int> valeurs)
    {
        using var flux = new MemoryStream();
        using (var w = new Utf8JsonWriter(flux))
        {
            w.WriteStartObject();
            w.WriteString("format", Format);
            w.WriteStartObject("reglages");
            foreach (var r in Liste)
                if (valeurs.TryGetValue(r.Cle, out var v)) w.WriteNumber(r.Cle, v);
            foreach (var (cle, v) in valeurs.Where(x => Trouver(x.Key) is null)) w.WriteNumber(cle, v);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(flux.ToArray());
    }

    /// <summary>
    /// Lecture STRICTE des données reçues (poste) : objet avec « format » = reglages-v1 et « reglages » seulement ; chaque valeur un
    /// nombre entier ; puis Controler. Erreurs vides = valide ; les valeurs lues sont rendues dans l'ordre de la liste fermée.
    /// </summary>
    public static (IReadOnlyDictionary<string, int> Valeurs, IReadOnlyList<string> Erreurs) Lire(string? json)
    {
        var valeurs = new Dictionary<string, int>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(json) || Encoding.UTF8.GetByteCount(json) > TailleMax) return (valeurs, ["Réglages communs : données absentes ou trop longues."]);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var racine = doc.RootElement;
            if (racine.ValueKind != JsonValueKind.Object || racine.EnumerateObject().Any(p => p.Name is not ("format" or "reglages"))
                || !racine.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != Format
                || !racine.TryGetProperty("reglages", out var r) || r.ValueKind != JsonValueKind.Object)
                return (valeurs, [$"Réglages communs : format non pris en charge (attendu : {Format})."]);
            foreach (var p in r.EnumerateObject())
            {
                if (Trouver(p.Name) is null) return (valeurs, [$"Réglage hors de la liste permise : {Court(p.Name)}."]);
                if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var v))
                    return (valeurs, [$"{Trouver(p.Name)!.Libelle} : nombre entier attendu."]);
                if (!valeurs.TryAdd(p.Name, v)) return (valeurs, [$"{Trouver(p.Name)!.Libelle} : donné deux fois."]);
            }
        }
        catch (JsonException)
        {
            return (valeurs, ["Réglages communs : données illisibles."]);
        }
        var (_, erreurs) = Controler(valeurs);
        return (Liste.Where(x => valeurs.ContainsKey(x.Cle)).ToDictionary(x => x.Cle, x => valeurs[x.Cle], StringComparer.Ordinal), erreurs);
    }

    /// <summary>Texte court d'un jeu : « Péremption : alerte orange 120 jours ; … » (écrans).</summary>
    public static string Texte(IReadOnlyDictionary<string, int> valeurs) =>
        string.Join(" ; ", Liste.Where(r => valeurs.ContainsKey(r.Cle))
            .Select(r => $"{r.Libelle} {valeurs[r.Cle].ToString("N0", CultureInfo.GetCultureInfo("fr-FR"))} {r.Unite}"));

    private static string Court(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
