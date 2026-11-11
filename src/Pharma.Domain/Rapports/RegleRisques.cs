namespace Pharma.Domain.Rapports;

/// <summary>Une journée d'activité : perte constatée (manque de caisse et boîtes manquantes ajustées) et employés présents.</summary>
public sealed record JourneePerte(DateOnly Jour, decimal Perte, IReadOnlyCollection<long> Presents);

/// <summary>
/// Pertes rapprochées de la présence d'un employé sur la période : jours présent, jours de perte en sa présence, perte moyenne
/// les jours où il est là et les jours où il n'est pas là. Signale l'employé dont la présence coïncide nettement avec les pertes.
/// </summary>
public sealed record PresencePertes(int JoursPresent, int JoursAvecPerte, decimal PerteEnPresence, decimal MoyennePresent, decimal MoyenneAbsent,
    bool Signale);

public static class RegleRisques
{
    /// <summary>Jours de perte en sa présence à partir desquels l'employé peut être signalé.</summary>
    public const int JoursPerteMin = 3;
    /// <summary>Jours d'absence nécessaires pour comparer (un employé présent tous les jours n'est jamais signalé).</summary>
    public const int JoursAbsentMin = 3;
    /// <summary>Perte moyenne les jours de présence au moins égale à ce multiple de la perte moyenne les jours d'absence.</summary>
    public const decimal Facteur = 2m;

    public static PresencePertes Correler(IReadOnlyList<JourneePerte> jours, long employe)
    {
        var presents = jours.Where(j => j.Presents.Contains(employe)).ToList();
        var absents = jours.Where(j => !j.Presents.Contains(employe)).ToList();
        static decimal Moyenne(List<JourneePerte> l) => l.Count == 0 ? 0 : Math.Round(l.Sum(j => j.Perte) / l.Count, 2, MidpointRounding.AwayFromZero);
        var (mp, ma) = (Moyenne(presents), Moyenne(absents));
        var avecPerte = presents.Count(j => j.Perte > 0);
        var signale = avecPerte >= JoursPerteMin && absents.Count >= JoursAbsentMin && mp > 0 && mp >= Facteur * ma;
        return new PresencePertes(presents.Count, avecPerte, presents.Sum(j => j.Perte), mp, ma, signale);
    }
}
