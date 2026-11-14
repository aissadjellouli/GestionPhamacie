using System.Globalization;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Financements;

/// <summary>
/// Source d'argent hors trésorerie, réservée au titulaire : un prêt bancaire (PretId) ou un apport du titulaire (PretId null).
/// Code échangé avec le poste : « titulaire » ou « pret:12 ».
/// </summary>
public sealed record SourceFinancement(long? PretId)
{
    public static readonly SourceFinancement Titulaire = new((long?)null);

    public bool EstTitulaire => PretId is null;
    public string Type => EstTitulaire ? "titulaire" : "pret";
    public string Code => PretId is { } id ? $"pret:{id.ToString(CultureInfo.InvariantCulture)}" : "titulaire";

    /// <summary>Null si aucun code n'est donné (pas de source) ; refus si le code est inconnu.</summary>
    public static SourceFinancement? Lire(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var c = code.Trim();
        if (c == "titulaire") return Titulaire;
        if (c.StartsWith("pret:", StringComparison.Ordinal) && long.TryParse(c[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
            return new SourceFinancement(id);
        throw new RegleMetierException("Source inconnue : choisissez un prêt ou l’apport du titulaire.", "source");
    }
}
