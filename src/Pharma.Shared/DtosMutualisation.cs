using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Pharma.Shared;

// ---- Format d'échange mutu-v1 (lot M1, docs/conception-synchronisation.md § 16 bis.3) ----
// Valeur du modèle d'une table de réglages (groupe 2) publiée par le serveur central. Transporté dans le champ « donneesJson » d'un
// élément descendant du contrat desc-v1 (table locale « reglage_central », clé « mutu_<table> »), sans nouveau contrat de transport.
// Un champ ajouté sans changement de sens impose mutu-v2 : la lecture est stricte (champ inconnu refusé). Aucun identifiant propre à une
// officine (produit_id, cree_par, id, horaires d'ouverture…) : une remise vise une catégorie, un motif est identifié par (liste, code).
// Les règles métier sont dans Pharma.Domain (ValidationModele) : ce fichier ne décrit que la forme et la sérialisation.

/// <summary>Constantes du format mutu-v1.</summary>
public static class ContratMutu
{
    public const string Format = "mutu-v1";
    public const string ModeImpose = "impose";
    public const string ModePropose = "propose";
    public const string ModeLocal = "local";
    /// <summary>Taille maximale d'un élément (octets UTF-8), comme la voie descendante (ClientServeurCentralHttp.MaxDonnees).</summary>
    public const int TailleMax = 64 * 1024;
    public const string PrefixeCle = "mutu_";

    /// <summary>Les 11 tables du groupe 2 (même liste que Pharma.Domain.Reglages.TablesMutualisables, vérifiée par un test).</summary>
    public static readonly IReadOnlyList<string> Tables =
    [
        "parametre_fiscal", "regle_alerte_psychotrope", "parametre_tracabilite", "parametre_serialisation", "parametre_substitution",
        "motif_reglable", "parametre_formulaire_sms", "parametre_surveillance", "parametre_alerte", "parametre_inventaire", "remise_automatique"
    ];

    public static string Cle(string table) => PrefixeCle + table;
}

/// <summary>Erreur de forme d'un élément mutu-v1 (à l'écriture ou à la lecture) : message en français, sans donnée personnelle.</summary>
public sealed class FormatMutuException(string message) : Exception(message);

/// <summary>
/// Élément mutu-v1 d'une table : mode, version du modèle, et, sauf en mode « local » (libération), ajouts locaux (motifs et remises seulement),
/// date de publication (UTC, à la seconde), auteur au central et valeur.
/// </summary>
public sealed record ElementMutuDto(string Mode, int VersionModele, bool? AjoutsLocaux = null, DateTime? PublieLeUtc = null, string? Auteur = null,
    ValeurMutuDto? Valeur = null)
{
    public static ElementMutuDto Local(int versionModele) => new(ContratMutu.ModeLocal, versionModele);
}

/// <summary>Valeur d'une table (noms de colonnes de la base, sans id, officine_id, modifie_le, modifie_par).</summary>
public abstract record ValeurMutuDto;

public sealed record MutuFiscalDto(
    [property: JsonPropertyName("conservation_annees")] int ConservationAnnees,
    [property: JsonPropertyName("format_export_dgi")] string FormatExportDgi,
    [property: JsonPropertyName("separateur_csv")] string SeparateurCsv,
    [property: JsonPropertyName("fuseau")] string Fuseau) : ValeurMutuDto;

public sealed record MutuRegleAlertePsychotropeDto(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("active")] bool Active,
    [property: JsonPropertyName("seuil")] int Seuil,
    [property: JsonPropertyName("periode_jours")] int? PeriodeJours,   // vide (script 153) : sans limite ou sans objet
    [property: JsonPropertyName("exiger_valideur")] bool ExigerValideur,
    [property: JsonPropertyName("unites_par_jour")] int? UnitesParJour);

/// <summary>
/// Les règles à code fixe (ni libellé ni ordre : affichage local) : 5 obligatoires, plus « traitement_en_cours » (script 153), facultative
/// dans un modèle (absente : la règle de l'officine est gardée).
/// </summary>
public sealed record MutuAlertesPsychotropesDto(
    [property: JsonPropertyName("regles")] IReadOnlyList<MutuRegleAlertePsychotropeDto> Regles) : ValeurMutuDto;

public sealed record MutuTracabiliteDto(
    [property: JsonPropertyName("boite_obligatoire")] bool BoiteObligatoire,
    [property: JsonPropertyName("patient_obligatoire")] string PatientObligatoire) : ValeurMutuDto;

/// <summary>Le connecteur est fixé à « factice » par la base : il ne fait pas partie du modèle.</summary>
public sealed record MutuSerialisationDto(
    [property: JsonPropertyName("actif")] bool Actif,
    [property: JsonPropertyName("factice_hors_ligne")] bool FacticeHorsLigne) : ValeurMutuDto;

public sealed record MutuSubstitutionDto(
    [property: JsonPropertyName("equivalent_exact_seulement")] bool EquivalentExactSeulement) : ValeurMutuDto;

public sealed record MutuMotifDto(
    [property: JsonPropertyName("liste")] string Liste,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("libelle")] string Libelle,
    [property: JsonPropertyName("actif")] bool Actif,
    [property: JsonPropertyName("ordre")] int Ordre,
    [property: JsonPropertyName("effet")] string Effet,
    [property: JsonPropertyName("detail")] string Detail);

/// <summary>Clé d'un motif : (liste, code) ; un motif nouveau du central a un code préfixé « c_ ». L'identifiant local n'est jamais transmis.</summary>
public sealed record MutuMotifsDto(
    [property: JsonPropertyName("motifs")] IReadOnlyList<MutuMotifDto> Motifs) : ValeurMutuDto;

/// <summary>Texte du formulaire d'accord SMS ; null = texte par défaut.</summary>
public sealed record MutuFormulaireSmsDto(
    [property: JsonPropertyName("texte")] string? Texte) : ValeurMutuDto;

/// <summary>Seuils du contrôle interne ; les horaires d'ouverture (heure_ouverture, heure_fermeture) restent locaux.</summary>
public sealed record MutuSurveillanceDto(
    [property: JsonPropertyName("seuil_lignes_retirees")] int SeuilLignesRetirees,
    [property: JsonPropertyName("seuil_tiroir")] int SeuilTiroir,
    [property: JsonPropertyName("seuil_attentes_abandonnees")] int SeuilAttentesAbandonnees,
    [property: JsonPropertyName("seuil_remises")] int SeuilRemises,
    [property: JsonPropertyName("seuil_remises_patient")] int SeuilRemisesPatient,
    [property: JsonPropertyName("seuil_ajustements")] int SeuilAjustements,
    [property: JsonPropertyName("seuil_casses")] int SeuilCasses,
    [property: JsonPropertyName("seuil_echantillons")] int SeuilEchantillons,
    [property: JsonPropertyName("jours_comptage_sensibles")] int JoursComptageSensibles,
    [property: JsonPropertyName("reception_aveugle")] bool ReceptionAveugle,
    [property: JsonPropertyName("jours_avoir_retour")] int JoursAvoirRetour,
    [property: JsonPropertyName("seuil_credit_nouveau_patient")] decimal SeuilCreditNouveauPatient,
    [property: JsonPropertyName("jours_patient_recent")] int JoursPatientRecent,
    [property: JsonPropertyName("seuil_credits_jour")] decimal SeuilCreditsJour,
    [property: JsonPropertyName("jours_credit_sans_remboursement")] int JoursCreditSansRemboursement,
    [property: JsonPropertyName("seuil_autorisations_valideur")] int SeuilAutorisationsValideur,
    [property: JsonPropertyName("seuil_autorisations_employe")] int SeuilAutorisationsEmploye,
    [property: JsonPropertyName("minutes_postes_simultanes")] int MinutesPostesSimultanes,
    [property: JsonPropertyName("seuil_changements_poste")] int SeuilChangementsPoste,
    [property: JsonPropertyName("seuil_baisses_prix")] int SeuilBaissesPrix,
    [property: JsonPropertyName("seuil_sorties_tiroir")] int SeuilSortiesTiroir) : ValeurMutuDto;

/// <summary>Réglage général des seuils d'alerte (les colonnes que l'écran Administration › Seuils règle).</summary>
public sealed record MutuAlertesDto(
    [property: JsonPropertyName("jours_orange")] int JoursOrange,
    [property: JsonPropertyName("jours_rouge")] int JoursRouge,
    [property: JsonPropertyName("horizon_risque_jours")] int HorizonRisqueJours,
    [property: JsonPropertyName("delai_fournisseur_jours")] int DelaiFournisseurJours,
    [property: JsonPropertyName("jours_securite")] int JoursSecurite,
    [property: JsonPropertyName("jours_cible_commande")] int JoursCibleCommande,
    [property: JsonPropertyName("jours_dormant")] int JoursDormant,
    [property: JsonPropertyName("seuil_lot_delaisse")] int SeuilLotDelaisse,
    [property: JsonPropertyName("seuil_lot_delaisse_collectif")] int SeuilLotDelaisseCollectif) : ValeurMutuDto;

public sealed record MutuInventaireDto(
    [property: JsonPropertyName("seuil_boites")] int SeuilBoites,
    [property: JsonPropertyName("seuil_montant")] decimal SeuilMontant) : ValeurMutuDto;

/// <summary>Remise par catégorie (D6) ; Cle = identifiant stable du central (« c_ » + 12 caractères). Jamais de produit_id ni de cree_par.</summary>
public sealed record MutuRemiseDto(
    [property: JsonPropertyName("cle")] string Cle,
    [property: JsonPropertyName("categorie")] string Categorie,
    [property: JsonPropertyName("pourcentage")] decimal Pourcentage,
    [property: JsonPropertyName("date_debut")] DateOnly DateDebut,
    [property: JsonPropertyName("date_fin")] DateOnly DateFin,
    [property: JsonPropertyName("motif")] string? Motif);

public sealed record MutuRemisesDto(
    [property: JsonPropertyName("remises")] IReadOnlyList<MutuRemiseDto> Remises) : ValeurMutuDto;

/// <summary>
/// Sérialisation déterministe mutu-v1 : JSON UTF-8 compact, clés triées par ordinal (récursivement), listes triées par clé métier
/// (règles par code, motifs par liste puis code, remises par clé), nombres sous forme canonique (sans zéro final), dates « AAAA-MM-JJ »,
/// publication « AAAA-MM-JJTHH:MM:SSZ » (UTC), échappement minimal. Deux sérialisations d'un même modèle donnent les mêmes octets.
/// Lecture stricte : champ inconnu, manquant ou mal typé refusé ; taille bornée à 64 Kio.
/// </summary>
public static class SerialisationMutuV1
{
    private static readonly JsonWriterOptions OptionsEcriture = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    private static readonly JsonSerializerOptions OptionsValeur = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    private static readonly Dictionary<string, Type> TypeParTable = new(StringComparer.Ordinal)
    {
        ["parametre_fiscal"] = typeof(MutuFiscalDto),
        ["regle_alerte_psychotrope"] = typeof(MutuAlertesPsychotropesDto),
        ["parametre_tracabilite"] = typeof(MutuTracabiliteDto),
        ["parametre_serialisation"] = typeof(MutuSerialisationDto),
        ["parametre_substitution"] = typeof(MutuSubstitutionDto),
        ["motif_reglable"] = typeof(MutuMotifsDto),
        ["parametre_formulaire_sms"] = typeof(MutuFormulaireSmsDto),
        ["parametre_surveillance"] = typeof(MutuSurveillanceDto),
        ["parametre_alerte"] = typeof(MutuAlertesDto),
        ["parametre_inventaire"] = typeof(MutuInventaireDto),
        ["remise_automatique"] = typeof(MutuRemisesDto)
    };

    private static bool PorteAjoutsLocaux(string table) => table is "motif_reglable" or "remise_automatique";

    private static Type TypeDe(string table) =>
        TypeParTable.TryGetValue(table, out var t) ? t : throw new FormatMutuException($"Table « {table} » : elle ne fait pas partie des réglages mutualisables.");

    public static byte[] Serialiser(string table, ElementMutuDto element)
    {
        var type = TypeDe(table);
        var racine = new JsonObject { ["format"] = ContratMutu.Format, ["mode"] = element.Mode, ["version_modele"] = element.VersionModele };
        if (element.Mode == ContratMutu.ModeLocal)
        {
            if (element.Valeur is not null || element.AjoutsLocaux is not null || element.PublieLeUtc is not null || element.Auteur is not null)
                throw new FormatMutuException("Mode « local » : ni valeur, ni date, ni auteur, ni ajouts locaux.");
        }
        else if (element.Mode is ContratMutu.ModeImpose or ContratMutu.ModePropose)
        {
            if (element.Valeur is null || element.Valeur.GetType() != type)
                throw new FormatMutuException($"La valeur ne correspond pas à la table « {table} ».");
            if (string.IsNullOrWhiteSpace(element.Auteur)) throw new FormatMutuException("L’auteur de la publication est obligatoire.");
            if (element.PublieLeUtc is null) throw new FormatMutuException("La date de publication est obligatoire.");
            if (PorteAjoutsLocaux(table) != (element.AjoutsLocaux is not null))
                throw new FormatMutuException("« ajouts_locaux » se donne pour les motifs et les remises automatiques, et pour eux seuls.");
            if (element.AjoutsLocaux is { } ajouts) racine["ajouts_locaux"] = ajouts;
            racine["publie_le"] = Horodatage(element.PublieLeUtc.Value);
            racine["auteur"] = element.Auteur;
            racine["valeur"] = JsonSerializer.SerializeToNode(Trier(element.Valeur), type, OptionsValeur);
        }
        else throw new FormatMutuException("Mode : « impose », « propose » ou « local ».");

        using var doc = JsonDocument.Parse(racine.ToJsonString(OptionsValeur));
        using var flux = new MemoryStream();
        using (var w = new Utf8JsonWriter(flux, OptionsEcriture)) EcrireCanonique(w, doc.RootElement);
        var octets = flux.ToArray();
        if (octets.Length > ContratMutu.TailleMax)
            throw new FormatMutuException($"Le modèle fait {octets.Length} octets : au plus {ContratMutu.TailleMax / 1024} Kio.");
        return octets;
    }

    /// <summary>Lecture stricte (forme seulement : les règles métier sont celles de Pharma.Domain.Reglages.ValidationModele).</summary>
    public static ElementMutuDto Lire(string table, ReadOnlySpan<byte> octets)
    {
        var type = TypeDe(table);
        if (octets.Length > ContratMutu.TailleMax)
            throw new FormatMutuException($"Le modèle dépasse {ContratMutu.TailleMax / 1024} Kio.");
        JsonDocument doc;
        try { doc = JsonDocument.Parse(octets.ToArray()); }
        catch (JsonException) { throw new FormatMutuException("Le modèle n’est pas un JSON valide."); }
        using (doc)
        {
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) throw new FormatMutuException("Un objet JSON est attendu.");
            var inconnus = r.EnumerateObject().Select(p => p.Name)
                .Where(n => n is not ("format" or "mode" or "version_modele" or "ajouts_locaux" or "publie_le" or "auteur" or "valeur")).ToList();
            if (inconnus.Count > 0) throw new FormatMutuException($"Champ inconnu « {inconnus[0]} ».");

            var format = Texte(r, "format");
            if (format != ContratMutu.Format) throw new FormatMutuException($"Format « {format} » non pris en charge (attendu : {ContratMutu.Format}).");
            var mode = Texte(r, "mode");
            if (!r.TryGetProperty("version_modele", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                throw new FormatMutuException("« version_modele » doit être un nombre entier.");

            if (mode == ContratMutu.ModeLocal)
            {
                if (r.EnumerateObject().Count() != 3) throw new FormatMutuException("Mode « local » : ni valeur, ni date, ni auteur, ni ajouts locaux.");
                return ElementMutuDto.Local(version);
            }
            if (mode is not (ContratMutu.ModeImpose or ContratMutu.ModePropose))
                throw new FormatMutuException("Mode : « impose », « propose » ou « local ».");

            bool? ajouts = null;
            if (PorteAjoutsLocaux(table))
            {
                if (!r.TryGetProperty("ajouts_locaux", out var a) || a.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new FormatMutuException("« ajouts_locaux » doit être vrai ou faux.");
                ajouts = a.GetBoolean();
            }
            else if (r.TryGetProperty("ajouts_locaux", out _))
                throw new FormatMutuException("« ajouts_locaux » n’existe que pour les motifs et les remises automatiques.");

            var publie = Texte(r, "publie_le");
            if (!DateTime.TryParseExact(publie, "yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var publieLe))
                throw new FormatMutuException("« publie_le » : AAAA-MM-JJTHH:MM:SSZ (heure UTC).");
            var auteur = Texte(r, "auteur");
            if (!r.TryGetProperty("valeur", out var val)) throw new FormatMutuException("« valeur » est manquante.");
            ValeurMutuDto? valeur;
            try { valeur = (ValeurMutuDto?)val.Deserialize(type, OptionsValeur); }
            catch (JsonException ex) { throw new FormatMutuException($"« valeur » : {ex.Message}"); }
            if (valeur is null) throw new FormatMutuException("« valeur » est manquante.");
            return new ElementMutuDto(mode, version, ajouts, publieLe, auteur, valeur);
        }
    }

    private static string Texte(JsonElement e, string nom) =>
        e.TryGetProperty(nom, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatMutuException($"« {nom} » doit être un texte.");

    /// <summary>Listes dans l'ordre de la clé métier (ordinal) : le même modèle donne les mêmes octets.</summary>
    private static ValeurMutuDto Trier(ValeurMutuDto v) => v switch
    {
        MutuAlertesPsychotropesDto a => a with { Regles = a.Regles.OrderBy(x => x.Code, StringComparer.Ordinal).ToList() },
        MutuMotifsDto m => m with { Motifs = m.Motifs.OrderBy(x => x.Liste, StringComparer.Ordinal).ThenBy(x => x.Code, StringComparer.Ordinal).ToList() },
        MutuRemisesDto r => r with { Remises = r.Remises.OrderBy(x => x.Cle, StringComparer.Ordinal).ToList() },
        _ => v
    };

    private static string Horodatage(DateTime d)
    {
        var utc = d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    private static void EcrireCanonique(Utf8JsonWriter w, JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                w.WriteStartObject();
                foreach (var p in e.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    w.WritePropertyName(p.Name);
                    EcrireCanonique(w, p.Value);
                }
                w.WriteEndObject();
                break;
            case JsonValueKind.Array:
                w.WriteStartArray();
                foreach (var x in e.EnumerateArray()) EcrireCanonique(w, x);
                w.WriteEndArray();
                break;
            case JsonValueKind.String: w.WriteStringValue(e.GetString()); break;
            case JsonValueKind.Number: w.WriteRawValue(NombreCanonique(e), skipInputValidation: true); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }

    /// <summary>5000.00 et 5000 donnent « 5000 » : la forme ne dépend pas de l'échelle du décimal.</summary>
    private static string NombreCanonique(JsonElement e)
    {
        if (!e.TryGetDecimal(out var d)) return e.GetRawText();
        // Diviser par 1,000…0 retire les zéros finals de l'échelle
        return (d / 1.0000000000000000000000000000m).ToString(CultureInfo.InvariantCulture);
    }
}
