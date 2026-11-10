namespace Pharma.Application.Commun;

/// <summary>Page demandée par un écran : numéro (à partir de 1) et nombre de lignes par page.</summary>
public sealed record DemandePage(int Page = 1, int Taille = DemandePage.TailleParDefaut)
{
    public const int TailleParDefaut = 10;
    public const int TailleMin = 5;
    public const int TailleMax = 200;

    /// <summary>Valeurs remises dans les bornes : page au moins 1, taille entre <see cref="TailleMin"/> et <see cref="TailleMax"/>.</summary>
    public static DemandePage Lire(int? page, int? taille) =>
        new(Math.Max(page ?? 1, 1), Math.Clamp(taille ?? TailleParDefaut, TailleMin, TailleMax));

    public int Decalage => (Page - 1) * Taille;
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
