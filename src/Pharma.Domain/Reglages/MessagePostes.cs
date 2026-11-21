using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pharma.Domain.Reglages;

/// <summary>
/// Lot S7.3 (reste) : message court envoyé par le titulaire à distance pour être affiché sur les postes d'une officine (ordre
/// « message » du titulaire, format message-v1 ; docs/ecrans-central.md § 3 nonies). Contrôlé TROIS fois avec les mêmes règles :
/// écran du central, base centrale (cloud_message_poste_controle, script central 038, et contrainte de ordre_central), poste à
/// la réception. Texte simple : 1 à 200 caractères, une seule ligne, ni balise (&lt; &gt;), ni caractère invisible ou de contrôle,
/// ni lien (adresse, « www. », protocole, nom de domaine courant). Affiché jusqu'à une date : 1 à 7 jours après l'envoi.
/// </summary>
public static partial class MessagePostes
{
    /// <summary>Type d'ordre qui porte un message (central et officine).</summary>
    public const string TypeOrdre = "message";
    public const string Format = "message-v1";
    public const int LongueurMax = 200;
    public const int JoursMin = 1;
    public const int JoursMax = 7;

    /// <summary>
    /// Caractères interdits : contrôles (dont retour à la ligne), invisibles et de sens d'écriture (U+200B à U+200F, U+2028 à U+202E,
    /// U+2060 à U+2069, U+FEFF), chevrons (aucune balise). Même classe que la base centrale (script 038).
    /// </summary>
    public const string MotifInterdits = @"[\u0001-\u001F\u007F-\u009F​-‏ -‮⁠-⁩﻿<>]";

    /// <summary>Lien (sans tenir compte de la casse) : « :// », « www. », protocole connu, nom de domaine courant. Même motif que la base.</summary>
    public const string MotifLien =
        @"://|www\.|(https?|ftp|file|mailto|javascript|data|tel|sms|whatsapp):|[a-z0-9-]\.(com|net|org|fr|dz|info|io|app|biz|me|co|ly|gl|link|xyz)([^a-z0-9]|$)";

    /// <summary>Date de fin écrite par le central : UTC à la seconde, « Z » ou décalage explicite (jamais une heure locale ambiguë).</summary>
    public const string MotifDate = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+-][0-9]{2}:[0-9]{2})$";

    /// <summary>Libellés des refus (codes communs avec la base centrale).</summary>
    public static readonly IReadOnlyDictionary<string, string> Refus = new Dictionary<string, string>
    {
        ["format"] = "message illisible",
        ["vide"] = "message vide",
        ["trop_long"] = $"message de plus de {LongueurMax} caractères",
        ["texte"] = "message en texte simple seulement (une ligne, sans balise ni caractère invisible)",
        ["lien"] = "message sans lien ni adresse de site",
        ["duree"] = $"durée d'affichage de {JoursMin} à {JoursMax} jours",
    };

    /// <summary>Contrôle du texte saisi (rogné) : null s'il convient, sinon vide, trop_long, texte ou lien.</summary>
    public static string? ControlerTexte(string? texte)
    {
        var t = (texte ?? "").Trim();
        if (t.Length == 0) return "vide";
        if (t.EnumerateRunes().Count() > LongueurMax) return "trop_long";
        if (Interdits().IsMatch(t)) return "texte";
        if (Lien().IsMatch(t)) return "lien";
        return null;
    }

    /// <summary>Durée choisie à l'écran du central (liste bornée de 1 à 7 jours).</summary>
    public static bool JoursValides(int jours) => jours is >= JoursMin and <= JoursMax;

    /// <summary>
    /// Lecture des données d'un ordre « message » (poste, à la réception) : format, clés, texte et durée (fin après la création, 7 jours
    /// au plus). Code null : message valable ; sinon le code du refus (format, vide, trop_long, texte, lien, duree).
    /// </summary>
    public static (string? Texte, DateTime? JusquAuUtc, string? Code) Lire(string? donneesJson, DateTime creeLeUtc)
    {
        try
        {
            using var doc = JsonDocument.Parse(donneesJson ?? "");
            var d = doc.RootElement;
            if (d.ValueKind != JsonValueKind.Object || (donneesJson ?? "").Length > 2000
                || d.EnumerateObject().Any(p => p.Name is not ("format" or "texte" or "jusqu_au"))
                || !d.TryGetProperty("format", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() != Format
                || !d.TryGetProperty("texte", out var t) || t.ValueKind != JsonValueKind.String
                || !d.TryGetProperty("jusqu_au", out var j) || j.ValueKind != JsonValueKind.String)
                return (null, null, "format");
            var texte = t.GetString()!;
            // Le central écrit le texte déjà rogné : un espace en tête ou en fin signale une donnée qui n'a pas suivi le chemin normal
            if (texte != texte.Trim()) return (null, null, "texte");
            if (ControlerTexte(texte) is { } refus) return (null, null, refus);
            if (!Date().IsMatch(j.GetString()!)
                || !DateTimeOffset.TryParse(j.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var fin))
                return (null, null, "format");
            var finUtc = fin.UtcDateTime;
            var cree = creeLeUtc.Kind == DateTimeKind.Local ? creeLeUtc.ToUniversalTime() : DateTime.SpecifyKind(creeLeUtc, DateTimeKind.Utc);
            if (finUtc <= cree || finUtc > cree.AddDays(JoursMax)) return (null, null, "duree");
            return (texte, finUtc, null);
        }
        catch (JsonException)
        {
            return (null, null, "format");
        }
    }

    [GeneratedRegex(MotifInterdits)]
    private static partial Regex Interdits();

    [GeneratedRegex(MotifLien, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Lien();

    [GeneratedRegex(MotifDate)]
    private static partial Regex Date();
}
