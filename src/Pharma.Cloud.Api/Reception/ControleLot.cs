using Pharma.Shared;

namespace Pharma.Cloud.Api.Reception;

/// <summary>Résultat du contrôle d'un lot reçu, avant toute écriture.</summary>
public sealed record LotControle(LotSyncDto? Lot, int CodeHttp, string? Refus)
{
    public bool Valide => Lot is not null && Refus is null;
}

/// <summary>
/// Contrôles d'un lot sync-v1 déposé (indépendants de la base) : taille maximale, forme et version du contrat, officine de l'en-tête
/// = officine du certificat (jamais l'inverse), empreinte SHA-256 juste, contenu exactement sous forme canonique (les octets gardés
/// pour preuve sont ceux que l'empreinte couvre).
/// </summary>
public static class ControleLot
{
    /// <summary>Plus grand lot accepté (borne haute de sync_parametre.lot_max_octets, 16 Mio, plus l'en-tête).</summary>
    public const int TailleMax = 17 * 1024 * 1024;
    public static readonly IReadOnlySet<string> VersionsAcceptees = new HashSet<string>(StringComparer.Ordinal) { ContratSync.VersionV1 };

    /// <summary>Message générique rendu à l'appelant (le détail ne va qu'au journal du serveur central).</summary>
    public static string MessageClient(int codeHttp) => codeHttp switch
    {
        403 => "Lot refusé : officine non autorisée pour ce certificat.",
        413 => "Lot refusé : taille maximale dépassée.",
        _ => "Lot refusé : contenu non conforme au contrat de synchronisation (détail au journal du serveur central)."
    };

    public static LotControle Controler(ReadOnlySpan<byte> contenu, Guid officineDuCertificat)
    {
        if (contenu.Length == 0) return new(null, 400, "Lot vide.");
        if (contenu.Length > TailleMax) return new(null, 413, $"Lot trop grand ({contenu.Length} octets, maximum {TailleMax}).");
        LotSyncDto lot;
        try { lot = SerialisationSyncV1.Lire(contenu); }
        catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return new(null, 400, "Lot illisible ou non conforme au contrat : " + e.Message);
        }
        if (!VersionsAcceptees.Contains(lot.EnTete.VersionContrat)) return new(null, 422, $"Version de contrat {lot.EnTete.VersionContrat} non prise en charge.");
        if (lot.EnTete.OfficineId != officineDuCertificat) return new(null, 403, "Lot d'une autre officine que celle du certificat.");
        if (!SerialisationSyncV1.EmpreinteValide(lot)) return new(null, 422, "Empreinte du lot fausse.");
        if (!SerialisationSyncV1.Serialiser(lot).AsSpan().SequenceEqual(contenu)) return new(null, 422, "Lot non canonique (sync-v1).");
        return new(lot, 200, null);
    }
}
