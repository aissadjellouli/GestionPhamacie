using System.Text.RegularExpressions;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Stock;

public enum ZoneEmplacement { Vente, Reserve }

/// <summary>Code d'un emplacement : rayon, étagère, tiroir séparés par des tirets (ex. A-02-3), en majuscules.</summary>
public static partial class RegleEmplacement
{
    [GeneratedRegex(@"^[A-Z0-9]{1,10}$")]
    private static partial Regex Segment();

    public static string Code(this ZoneEmplacement z) => z == ZoneEmplacement.Vente ? "vente" : "reserve";
    public static ZoneEmplacement ZoneDepuisCode(string code) => code == "reserve" ? ZoneEmplacement.Reserve : ZoneEmplacement.Vente;

    /// <summary>Normalise et vérifie les trois niveaux, puis compose le code (étagère et tiroir facultatifs).</summary>
    public static (string Rayon, string? Etagere, string? Tiroir, string Code) Composer(string? rayon, string? etagere, string? tiroir)
    {
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim().ToUpperInvariant();
        var (r, e, t) = (Net(rayon), Net(etagere), Net(tiroir));
        if (r is null) throw new RegleMetierException("Le rayon est obligatoire (ex. A).");
        foreach (var (valeur, nom) in new[] { (r, "Rayon"), (e, "Étagère"), (t, "Tiroir") })
            if (valeur is not null && !Segment().IsMatch(valeur))
                throw new RegleMetierException($"{nom} : lettres et chiffres seulement, 10 caractères au plus.");
        if (t is not null && e is null) throw new RegleMetierException("Un tiroir se trouve sur une étagère : indiquez l’étagère.");
        return (r, e, t, string.Join('-', new[] { r, e, t }.Where(x => x is not null)));
    }

    /// <summary>Le rangement d'un produit : vente en zone de vente, réserve en zone de réserve, et pas deux fois le même.</summary>
    public static void VerifierRangement(ZoneEmplacement? zoneVente, ZoneEmplacement? zoneReserve, long? venteId, long? reserveId)
    {
        if (venteId is not null && zoneVente != ZoneEmplacement.Vente)
            throw new RegleMetierException("L’emplacement de vente doit être en zone de vente.");
        if (reserveId is not null && zoneReserve != ZoneEmplacement.Reserve)
            throw new RegleMetierException("L’emplacement de réserve doit être en zone de réserve.");
        if (venteId is not null && venteId == reserveId)
            throw new RegleMetierException("La vente et la réserve doivent être deux emplacements différents.");
    }
}
