using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Rapports;

namespace Pharma.Application.Surveillance;

// ---- Écarts dans le temps et risques par employé (titulaire seul) ----

/// <summary>
/// Une journée d'activité : écart de caisse des clôtures du jour (Ecart signé, Manque = somme des écarts négatifs), boîtes manquantes
/// ajustées en inventaire (PerteStock au prix d'achat), boîtes fantômes signalées, anomalies, employés présents et postes.
/// </summary>
public sealed record JourEcarts(DateOnly Jour, decimal Ecart, decimal Manque, decimal PerteStock, int BoitesAjustees, decimal ValeurFantomes,
    int Anomalies, int Urgentes, IReadOnlyList<long> Presents, IReadOnlyList<string> Postes)
{
    public decimal Perte => Manque + PerteStock;
}

/// <summary>Ce que l'employé a fait sur la période (compteurs lus en base).</summary>
public sealed record ActiviteEmploye(long UtilisateurId, string Nom, bool Actif, decimal ManqueCaisse, int Clotures, int Anomalies, int Urgentes,
    int Annulations, int RetoursClients, int Remises, decimal Credits, int AutorisationsDemandees, int AutorisationsDonnees, int Casses,
    int Echantillons, int BoitesAjustees);

public sealed record RisqueEmploye(ActiviteEmploye Activite, PresencePertes Presence);

public sealed record TableauRisques(DateOnly Du, DateOnly Au, IReadOnlyList<JourEcarts> Jours, IReadOnlyList<RisqueEmploye> Employes)
{
    public IReadOnlyDictionary<long, string> Noms => Employes.ToDictionary(e => e.Activite.UtilisateurId, e => e.Activite.Nom);
}

public interface IRisques
{
    /// <summary>Journées de la période où il y a eu de l'activité ou une perte (les autres sont omises).</summary>
    Task<IReadOnlyList<JourEcarts>> JoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
    /// <summary>Employés actifs ou présents sur la période, avec leurs compteurs.</summary>
    Task<IReadOnlyList<ActiviteEmploye>> EmployesAsync(DateOnly du, DateOnly au, CancellationToken ct = default);
}

public sealed class ObtenirTableauRisques(IRisques risques, IPermissionService droits, IHorloge horloge)
{
    public const int JoursParDefaut = 30;
    public const int JoursMax = 366;

    public async Task<TableauRisques> ExecuterAsync(DateOnly? du, DateOnly? au, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les écarts et les risques par employé", ct);
        var fin = au ?? horloge.Aujourdhui;
        var debut = du ?? fin.AddDays(-(JoursParDefaut - 1));
        if (fin < debut) throw new RegleMetierException("La période est inversée.", "periode");
        if (fin.DayNumber - debut.DayNumber + 1 > JoursMax) throw new RegleMetierException($"Période de {JoursMax} jours au plus.", "periode");
        var jours = await risques.JoursAsync(debut, fin, ct);
        var journees = jours.Select(j => new JourneePerte(j.Jour, j.Perte, j.Presents.ToHashSet())).ToList();
        var employes = (await risques.EmployesAsync(debut, fin, ct))
            .Select(a => new RisqueEmploye(a, RegleRisques.Correler(journees, a.UtilisateurId)))
            // Signalés d'abord, puis la plus forte perte en leur présence
            .OrderByDescending(e => e.Presence.Signale).ThenByDescending(e => e.Presence.PerteEnPresence).ThenBy(e => e.Activite.Nom)
            .ToList();
        return new TableauRisques(debut, fin, jours, employes);
    }
}
