using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Pharma.Shared;

// ---- Contrat de synchronisation sync-v1 (lot S1, docs/conception-synchronisation.md § 3 et § 7) ----
// Partagé par l'API de l'officine et le futur serveur central : la même bibliothèque sérialise et vérifie, donc la forme
// et l'empreinte sont identiques des deux côtés. Un champ ajouté est facultatif pour la version précédente ; un champ retiré
// ou changé de sens impose une nouvelle version (sync-v2).

/// <summary>Versions et valeurs du contrat de synchronisation.</summary>
public static class ContratSync
{
    public const string VersionV1 = "sync-v1";
    public const string Creation = "creation";
    public const string Modification = "modification";
    public const string Suppression = "suppression";
    /// <summary>
    /// Ligne d'un chargement complet (lot S6) : état d'une donnée dans l'instantané, appliqué comme une création ; son n° est
    /// le repère du chargement (plus grand n° de file visible dans l'instantané).
    /// </summary>
    public const string Instantane = "instantane";
    public static readonly IReadOnlySet<string> Operations = new HashSet<string>(StringComparer.Ordinal) { Creation, Modification, Suppression, Instantane };
}

/// <summary>
/// En-tête d'un lot montant. L'identité du lot est (officine, époque, n° de lot) ; les lots d'une époque sont numérotés sans trou
/// à partir de 1. L'empreinte (SHA-256 hexadécimal minuscule) couvre tout le lot sérialisé avec une empreinte vide.
/// </summary>
public sealed record EnTeteLotSyncDto(string VersionContrat, Guid OfficineId, int Epoque, long NumeroLot, int NombreLignes, string Empreinte);

/// <summary>
/// Une modification d'une table montante. Identité stable = (officine de l'en-tête, Table, Id) ; Id = clé primaire locale
/// (valeurs jointes par « | »), ou code du patient pour la table patient. Numero = n° de la ligne dans la file locale :
/// pour une même donnée, l'état retenu est celui de plus grand Numero. DonneesJson : ligne déjà minimisée (null pour
/// une suppression).
/// </summary>
public sealed record LigneSyncDto(long Numero, string Table, string Id, string Operation, DateTime SurvenuLeUtc, string? DonneesJson);

/// <summary>Lot montant sync-v1.</summary>
public sealed record LotSyncDto(EnTeteLotSyncDto EnTete, IReadOnlyList<LigneSyncDto> Lignes);

/// <summary>
/// Sérialisation déterministe sync-v1 : JSON UTF-8 compact, propriétés dans un ordre fixe, dates UTC à la microseconde
/// (« AAAA-MM-JJTHH:MM:SS.ffffffZ »), données de chaque ligne réécrites en JSON canonique (clés triées par ordinal, récursivement,
/// nombres recopiés tels quels), échappement minimal. Deux sérialisations d'un même lot donnent les mêmes octets.
/// </summary>
public static class SerialisationSyncV1
{
    private static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

    public static byte[] Serialiser(LotSyncDto lot)
    {
        Valider(lot);
        using var flux = new MemoryStream();
        using (var w = new Utf8JsonWriter(flux, Options))
        {
            w.WriteStartObject();
            w.WriteString("version", lot.EnTete.VersionContrat);
            w.WriteString("officine", lot.EnTete.OfficineId.ToString("D"));
            w.WriteNumber("epoque", lot.EnTete.Epoque);
            w.WriteNumber("lot", lot.EnTete.NumeroLot);
            w.WriteNumber("nombre", lot.EnTete.NombreLignes);
            w.WriteString("empreinte", lot.EnTete.Empreinte);
            w.WriteStartArray("lignes");
            foreach (var l in lot.Lignes) EcrireLigne(w, l);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return flux.ToArray();
    }

    /// <summary>Taille en octets d'une ligne sérialisée (pour borner un lot).</summary>
    public static int TailleLigne(LigneSyncDto ligne)
    {
        using var flux = new MemoryStream();
        using (var w = new Utf8JsonWriter(flux, Options)) EcrireLigne(w, ligne);
        return (int)flux.Length + 1;
    }

    /// <summary>Empreinte SHA-256 (hexadécimal minuscule) du lot sérialisé avec une empreinte vide.</summary>
    public static string CalculerEmpreinte(LotSyncDto lot) =>
        Convert.ToHexStringLower(SHA256.HashData(Serialiser(lot with { EnTete = lot.EnTete with { Empreinte = "" } })));

    public static LotSyncDto AvecEmpreinte(LotSyncDto lot) => lot with { EnTete = lot.EnTete with { Empreinte = CalculerEmpreinte(lot) } };

    public static bool EmpreinteValide(LotSyncDto lot) =>
        lot.EnTete.Empreinte.Length == 64 && string.Equals(lot.EnTete.Empreinte, CalculerEmpreinte(lot), StringComparison.Ordinal);

    public static LotSyncDto Lire(ReadOnlySpan<byte> octets)
    {
        using var doc = JsonDocument.Parse(octets.ToArray());
        var r = doc.RootElement;
        var lignes = r.GetProperty("lignes").EnumerateArray().Select(l => new LigneSyncDto(
            l.GetProperty("n").GetInt64(),
            l.GetProperty("table").GetString()!,
            l.GetProperty("id").GetString()!,
            l.GetProperty("op").GetString()!,
            DateTime.ParseExact(l.GetProperty("le").GetString()!, "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
            l.GetProperty("donnees").ValueKind == JsonValueKind.Null ? null : l.GetProperty("donnees").GetRawText())).ToList();
        var lot = new LotSyncDto(new EnTeteLotSyncDto(
            r.GetProperty("version").GetString()!,
            Guid.Parse(r.GetProperty("officine").GetString()!),
            r.GetProperty("epoque").GetInt32(),
            r.GetProperty("lot").GetInt64(),
            r.GetProperty("nombre").GetInt32(),
            r.GetProperty("empreinte").GetString()!), lignes);
        Valider(lot);
        return lot;
    }

    /// <summary>Réécrit un JSON en forme canonique (clés triées par ordinal, compact).</summary>
    public static string Canonique(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var flux = new MemoryStream();
        using (var w = new Utf8JsonWriter(flux, Options)) EcrireCanonique(w, doc.RootElement);
        return Encoding.UTF8.GetString(flux.ToArray());
    }

    private static void EcrireLigne(Utf8JsonWriter w, LigneSyncDto l)
    {
        w.WriteStartObject();
        w.WriteNumber("n", l.Numero);
        w.WriteString("table", l.Table);
        w.WriteString("id", l.Id);
        w.WriteString("op", l.Operation);
        w.WriteString("le", Horodatage(l.SurvenuLeUtc));
        w.WritePropertyName("donnees");
        if (l.DonneesJson is null) w.WriteNullValue();
        else
        {
            using var doc = JsonDocument.Parse(l.DonneesJson);
            EcrireCanonique(w, doc.RootElement);
        }
        w.WriteEndObject();
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
            case JsonValueKind.Number: w.WriteRawValue(e.GetRawText(), skipInputValidation: true); break;
            case JsonValueKind.True: w.WriteBooleanValue(true); break;
            case JsonValueKind.False: w.WriteBooleanValue(false); break;
            default: w.WriteNullValue(); break;
        }
    }

    private static string Horodatage(DateTime d)
    {
        var utc = d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);
        // Microseconde : les horodatages PostgreSQL n'ont pas de précision plus fine
        utc = new DateTime(utc.Ticks - utc.Ticks % 10, DateTimeKind.Utc);
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }

    private static void Valider(LotSyncDto lot)
    {
        if (lot.EnTete.VersionContrat != ContratSync.VersionV1) throw new ArgumentException($"Version de contrat non prise en charge : {lot.EnTete.VersionContrat}.");
        if (lot.EnTete.NombreLignes != lot.Lignes.Count) throw new ArgumentException("Nombre de lignes de l'en-tête différent du lot.");
        if (lot.EnTete.NumeroLot < 1 || lot.EnTete.Epoque < 1) throw new ArgumentException("N° de lot et époque commencent à 1.");
        foreach (var l in lot.Lignes)
        {
            if (!ContratSync.Operations.Contains(l.Operation)) throw new ArgumentException($"Opération inconnue : {l.Operation}.");
            if ((l.Operation == ContratSync.Suppression) != (l.DonneesJson is null))
                throw new ArgumentException("Une suppression n'a pas de données ; une création ou une modification en a.");
        }
    }
}
