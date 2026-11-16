using System.Globalization;
using System.Text.Json;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;

namespace Pharma.Application.Reglages;

// ---- Mutualisation des réglages, lot M5 : lecture de l'état des 11 tables pour l'écran (docs/conception-synchronisation.md § 16 bis.5) ----

/// <summary>Lecture seule de la valeur actuelle d'une table de l'officine, au format de la valeur du modèle (pour la comparer au modèle proposé).</summary>
public interface ILectureValeurReglage
{
    Task<string> ValeurLocaleAsync(string table, string valeurModeleJson, CancellationToken ct = default);
}

/// <summary>Un écart entre la valeur de l'officine et celle du serveur central (texte prêt à afficher).</summary>
public sealed record EcartReglage(string Libelle, string Local, string Central);

/// <summary>État d'une table pour l'écran : état stocké, mode en vigueur (local tant que la synchronisation est désactivée), et, pour une valeur proposée non adoptée, les écarts.</summary>
public sealed record VueReglage(EtatReglage Etat, string Libelle, string ModeEnVigueur, string? Auteur, IReadOnlyList<EcartReglage> Ecarts);

/// <summary>Nombre maximal d'écarts renvoyés (un modèle fait au plus 64 Kio : motifs et remises peuvent être nombreux).</summary>
public static class LimitesReglages
{
    public const int EcartsMax = 500;
}

/// <summary>
/// Lit l'état des 11 tables (route GET /api/reglages-mutualises). Pour une table proposée mais non adoptée, ajoute la liste des écarts
/// entre la valeur de l'officine et celle du modèle. Aucune écriture ; ne dépend pas du serveur central.
/// </summary>
public sealed class ConsulterReglagesMutualises(IReglagesMutualisesDepot depot, ILectureValeurReglage lecture)
{
    public async Task<IReadOnlyList<VueReglage>> ListerAsync(CancellationToken ct = default)
    {
        var etats = (await depot.EtatsAsync(ct)).ToDictionary(e => e.Table);
        var liste = new List<VueReglage>();
        foreach (var table in TablesMutualisables.Toutes)
            if (etats.TryGetValue(table, out var e)) liste.Add(await VueAsync(e, ct));
        return liste;
    }

    public async Task<VueReglage> LireAsync(string table, CancellationToken ct = default)
    {
        if (!TablesMutualisables.Connue(table)) throw new RegleMetierException("Réglage inconnu.");
        var e = (await depot.EtatsAsync(ct)).FirstOrDefault(x => x.Table == table)
                ?? throw new InvalidOperationException($"État de la table {table} absent de reglage_mutualise (script 141).");
        return await VueAsync(e, ct);
    }

    private async Task<VueReglage> VueAsync(EtatReglage e, CancellationToken ct)
    {
        var (mode, _) = await depot.ModeEffectifAsync(e.Table, ct);
        string? auteur = null;
        IReadOnlyList<EcartReglage> ecarts = [];
        if (e.ModeleJson is not null && ComparaisonReglages.LireModele(e.ModeleJson) is { } m)
        {
            auteur = m.Auteur;
            // Valeur proposée non adoptée : l'écran montre les deux valeurs côte à côte (D2)
            if (mode == ValidationModele.ModePropose && e.Etat == EtatsReglage.NonAdopte && m.Valeur is not null)
                ecarts = ComparaisonReglages.Comparer(e.Table, await lecture.ValeurLocaleAsync(e.Table, m.Valeur, ct), m.Valeur);
        }
        return new VueReglage(e, LibellesReglages.Texte(e.Table), mode, auteur, ecarts);
    }
}

/// <summary>Comparaison de la valeur locale et de la valeur du modèle, champ par champ (ou motif par motif, remise par remise), en texte lisible.</summary>
public static class ComparaisonReglages
{
    public sealed record Modele(string? Auteur, string? Valeur);

    /// <summary>Auteur et valeur de l'élément mutu-v1 rangé (null si le JSON est illisible).</summary>
    public static Modele? LireModele(string elementJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(elementJson);
            var r = doc.RootElement;
            var auteur = r.TryGetProperty("auteur", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
            var valeur = r.TryGetProperty("valeur", out var v) && v.ValueKind == JsonValueKind.Object ? v.GetRawText() : null;
            return new Modele(auteur, valeur);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Écarts triés par libellé, au plus <see cref="LimitesReglages.EcartsMax"/>.</summary>
    public static IReadOnlyList<EcartReglage> Comparer(string table, string localJson, string modeleJson)
    {
        var local = Aplatir(table, localJson);
        var central = Aplatir(table, modeleJson);
        var ecarts = new List<EcartReglage>();
        foreach (var cle in central.Keys.Union(local.Keys).Order(StringComparer.Ordinal))
        {
            var l = local.GetValueOrDefault(cle);
            var c = central.GetValueOrDefault(cle);
            if (l.Texte == c.Texte) continue;
            ecarts.Add(new EcartReglage(c.Libelle ?? l.Libelle ?? cle, l.Texte ?? Absent, c.Texte ?? Absent));
            if (ecarts.Count >= LimitesReglages.EcartsMax) break;
        }
        return ecarts;
    }

    public const string Absent = "—";

    private static Dictionary<string, (string? Libelle, string? Texte)> Aplatir(string table, string json)
    {
        var resultat = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var racine = doc.RootElement;
        switch (table)
        {
            case TablesMutualisables.AlertesPsychotropes:
                foreach (var r in Liste(racine, "regles"))
                {
                    var code = Texte(r, "code");
                    foreach (var p in r.EnumerateObject().Where(p => p.Name != "code"))
                        resultat[$"{code}/{p.Name}"] = ($"{RegleAlertesPsychotropes.Nom(code)} : {LibellesChampsReglages.Texte(table, p.Name)}", Valeur(p.Value));
                }
                break;
            case TablesMutualisables.Motifs:
                foreach (var m in Liste(racine, "motifs"))
                    resultat[$"{Texte(m, "liste")}/{Texte(m, "code")}"] =
                        ($"Motif « {Texte(m, "libelle")} » ({Texte(m, "liste")})",
                            $"{Texte(m, "libelle")}, {(m.GetProperty("actif").GetBoolean() ? "actif" : "inactif")}, rang {m.GetProperty("ordre").GetRawText()}");
                break;
            case TablesMutualisables.Remises:
                foreach (var r in Liste(racine, "remises"))
                    resultat[Texte(r, "cle")] = ($"Remise de la catégorie « {Texte(r, "categorie")} »",
                        $"{Valeur(r.GetProperty("pourcentage"))} % du {Texte(r, "date_debut")} au {Texte(r, "date_fin")}"
                        + (r.TryGetProperty("motif", out var mo) && mo.ValueKind == JsonValueKind.String ? $" ({mo.GetString()})" : ""));
                break;
            default:
                foreach (var p in racine.EnumerateObject())
                    resultat[p.Name] = (LibellesChampsReglages.Texte(table, p.Name), Valeur(p.Value));
                break;
        }
        return resultat;
    }

    private static IEnumerable<JsonElement> Liste(JsonElement racine, string nom) =>
        racine.TryGetProperty(nom, out var l) && l.ValueKind == JsonValueKind.Array ? l.EnumerateArray() : [];

    private static string Texte(JsonElement e, string nom) => e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string Valeur(JsonElement v)
    {
        var texte = v.ValueKind switch
        {
            JsonValueKind.True => "Oui",
            JsonValueKind.False => "Non",
            JsonValueKind.Null => "Valeur par défaut",
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.TryGetDecimal(out var d) ? (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture) : v.GetRawText(),
            _ => v.GetRawText()
        };
        return texte.Length > 300 ? texte[..299] + "…" : texte;
    }
}

/// <summary>Libellés des champs des tables à ligne unique et des règles des psychotropes, pour afficher les écarts (repli : nom de la colonne).</summary>
public static class LibellesChampsReglages
{
    public static string Texte(string table, string champ) => Libelles.TryGetValue(champ, out var l) ? l : champ.Replace('_', ' ');

    private static readonly Dictionary<string, string> Libelles = new(StringComparer.Ordinal)
    {
        ["conservation_annees"] = "Conservation des documents (années)",
        ["format_export_dgi"] = "Format de l’export fiscal",
        ["separateur_csv"] = "Séparateur du fichier CSV",
        ["fuseau"] = "Fuseau horaire",
        ["boite_obligatoire"] = "Boîte obligatoire",
        ["patient_obligatoire"] = "Patient obligatoire",
        ["actif"] = "Actif",
        ["active"] = "Active",
        ["factice_hors_ligne"] = "Connecteur factice hors ligne",
        ["equivalent_exact_seulement"] = "Équivalent exact seulement",
        ["texte"] = "Texte du formulaire",
        ["seuil_boites"] = "Seuil en boîtes",
        ["seuil_montant"] = "Seuil en montant",
        ["seuil"] = "Seuil",
        ["periode_jours"] = "Période (jours)",
        ["exiger_valideur"] = "Valideur exigé",
        ["unites_par_jour"] = "Unités par jour",
        ["jours_orange"] = "Péremption : jours avant l’orange",
        ["jours_rouge"] = "Péremption : jours avant le rouge",
        ["horizon_risque_jours"] = "Horizon du risque (jours)",
        ["delai_fournisseur_jours"] = "Délai fournisseur (jours)",
        ["jours_securite"] = "Jours de sécurité",
        ["jours_cible_commande"] = "Jours cibles de commande",
        ["jours_dormant"] = "Jours avant produit dormant",
        ["seuil_lot_delaisse"] = "Seuil de lot délaissé",
        ["seuil_lot_delaisse_collectif"] = "Seuil de lot délaissé (collectif)",
        ["seuil_lignes_retirees"] = "Lignes retirées d’une vente",
        ["seuil_tiroir"] = "Ouvertures du tiroir",
        ["seuil_attentes_abandonnees"] = "Ventes en attente abandonnées",
        ["seuil_remises"] = "Remises accordées",
        ["seuil_remises_patient"] = "Remises à un même patient",
        ["seuil_ajustements"] = "Ajustements de stock",
        ["seuil_casses"] = "Casses déclarées",
        ["seuil_echantillons"] = "Échantillons sortis",
        ["jours_comptage_sensibles"] = "Jours entre deux comptages sensibles",
        ["reception_aveugle"] = "Réception à l’aveugle",
        ["jours_avoir_retour"] = "Jours pour un avoir de retour",
        ["seuil_credit_nouveau_patient"] = "Crédit à un nouveau patient",
        ["jours_patient_recent"] = "Jours d’un patient récent",
        ["seuil_credits_jour"] = "Crédits dans la journée",
        ["jours_credit_sans_remboursement"] = "Jours de crédit sans remboursement",
        ["seuil_autorisations_valideur"] = "Autorisations par valideur",
        ["seuil_autorisations_employe"] = "Autorisations par employé",
        ["minutes_postes_simultanes"] = "Minutes de postes simultanés",
        ["seuil_changements_poste"] = "Changements de poste",
        ["seuil_baisses_prix"] = "Baisses de prix",
        ["seuil_sorties_tiroir"] = "Sorties du tiroir"
    };
}
