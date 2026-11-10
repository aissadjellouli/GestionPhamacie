using System.Globalization;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Rapports;

/// <summary>Dates : début et fin choisis dans le calendrier.</summary>
public enum TypePeriode { Jour, Semaine, Mois, Dates }

/// <summary>Période d'un rapport, bornes incluses. La semaine va du lundi au dimanche.</summary>
public sealed record PeriodeRapport(TypePeriode Type, DateOnly Du, DateOnly Au)
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public const int JoursMax = 366;

    /// <summary>Au-delà, les ventes sont regroupées par semaine pour que le graphique reste lisible.</summary>
    public const int JoursDetailMax = 62;

    /// <summary>Jour, semaine ou mois qui contient la date de référence.</summary>
    public static PeriodeRapport Calculer(TypePeriode type, DateOnly reference) => type switch
    {
        TypePeriode.Jour or TypePeriode.Dates => new(TypePeriode.Jour, reference, reference),
        TypePeriode.Semaine => new(type, Lundi(reference), Lundi(reference).AddDays(6)),
        _ => new(type, new DateOnly(reference.Year, reference.Month, 1),
                 new DateOnly(reference.Year, reference.Month, DateTime.DaysInMonth(reference.Year, reference.Month)))
    };

    /// <summary>Dates choisies dans le calendrier : pas de fin avant le début, pas de jour futur, un an au plus.</summary>
    public static PeriodeRapport Choisie(DateOnly du, DateOnly au, DateOnly aujourdhui)
    {
        if (au < du) throw new RegleMetierException("La date de fin doit être après la date de début.");
        if (au > aujourdhui) throw new RegleMetierException("La date de fin ne peut pas être dans le futur.");
        if (au.DayNumber - du.DayNumber + 1 > JoursMax) throw new RegleMetierException("Période limitée à un an (366 jours).");
        return new(TypePeriode.Dates, du, au);
    }

    /// <summary>Même durée, juste avant (sens -1) ou juste après (sens +1).</summary>
    public PeriodeRapport Decaler(int sens) =>
        Type == TypePeriode.Dates
            ? this with { Du = Du.AddDays(sens * NombreJours), Au = Au.AddDays(sens * NombreJours) }
            : Calculer(Type, sens < 0 ? Du.AddDays(-1) : Au.AddDays(1));

    public static DateOnly Lundi(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    public int NombreJours => Au.DayNumber - Du.DayNumber + 1;

    public bool ParSemaine => NombreJours > JoursDetailMax;

    /// <summary>Ex. « Lundi 9 novembre 2026 », « Semaine du 9 au 15 novembre 2026 », « Novembre 2026 », « Du 3 au 15 novembre 2026 ».</summary>
    public string Libelle => Type switch
    {
        TypePeriode.Jour => Majuscule(Du.ToString("dddd d MMMM yyyy", Fr)),
        TypePeriode.Semaine => $"Semaine {Intervalle()}",
        TypePeriode.Dates when Du == Au => Majuscule(Du.ToString("dddd d MMMM yyyy", Fr)),
        TypePeriode.Dates => Majuscule(Intervalle()),
        _ => Majuscule(Du.ToString("MMMM yyyy", Fr))
    };

    private string Intervalle() =>
        Du.Year != Au.Year ? $"du {Date(Du, true)} au {Date(Au, true)}"
        : Du.Month != Au.Month ? $"du {Date(Du, false)} au {Date(Au, true)}"
        : $"du {Quantieme(Du)} au {Date(Au, true)}";

    /// <summary>« 1er novembre 2026 », « 9 novembre ».</summary>
    private static string Date(DateOnly d, bool annee) => $"{Quantieme(d)} {d.ToString(annee ? "MMMM yyyy" : "MMMM", Fr)}";

    private static string Quantieme(DateOnly d) => d.Day == 1 ? "1er" : d.Day.ToString(Fr);

    private static string Majuscule(string s) => s.Length == 0 ? s : char.ToUpper(s[0], Fr) + s[1..];
}
