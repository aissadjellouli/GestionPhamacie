namespace Pharma.Application.Commun;

/// <summary>Page demandée par un écran : numéro (à partir de 1) et nombre de lignes par page.</summary>
public sealed record DemandePage(int Page = 1, int Taille = DemandePage.TailleParDefaut)
{
    public const int TailleParDefaut = 10;
    public const int TailleMin = 5;
    public const int TailleMax = 200;

    /// <summary>Numéro de page le plus grand accepté (au-delà, la page est ramenée à cette valeur : liste vide, sans erreur).</summary>
    public const int PageMax = 1_000_000;

    /// <summary>
    /// Valeurs remises dans les bornes : page entre 1 et <see cref="PageMax"/>, taille entre <see cref="TailleMin"/> et <see cref="TailleMax"/>.
    /// </summary>
    public static DemandePage Lire(int? page, int? taille) =>
        new(Math.Clamp(page ?? 1, 1, PageMax), Math.Clamp(taille ?? TailleParDefaut, TailleMin, TailleMax));

    /// <summary>Nombre de lignes à sauter, calculé sans débordement même pour une page construite hors bornes.</summary>
    public int Decalage => (int)Math.Clamp(((long)Math.Max(Page, 1) - 1) * Math.Max(Taille, 0), 0, int.MaxValue);
}

/// <summary>Filtre d'un relevé de dette (patient ou grossiste) : période sur la date du mouvement, type de mouvement.</summary>
public sealed record FiltreReleve(DateOnly? Du = null, DateOnly? Au = null, string? Type = null)
{
    public static FiltreReleve Lire(DateOnly? du, DateOnly? au, string? type) => new(du, au, string.IsNullOrWhiteSpace(type) ? null : type.Trim());
}

/// <summary>Une page de résultats et le nombre total de lignes qui répondent aux filtres (toutes pages confondues).</summary>
public sealed record Page<T>(IReadOnlyList<T> Lignes, int Total, int Numero, int Taille)
{
    public Page<TAutre> Vers<TAutre>(Func<T, TAutre> convertir) => new(Lignes.Select(convertir).ToList(), Total, Numero, Taille);
}
