using Pharma.Application.Commun;
using Pharma.Domain.Alertes;

namespace Pharma.Application.Alertes;

public interface IAlertesEcriture
{
    /// <summary>Reporte une alerte ouverte. Faux si elle n'existe pas, n'est plus ouverte, ou n'est pas d'un des types donnés.</summary>
    /// <param name="types">Types que l'employé peut voir (AlertesSelonDroits) : une autre alerte n'est pas touchée.</param>
    Task<bool> ReporterAsync(long alerteId, DateOnly jusquAu, IReadOnlyCollection<string> types, CancellationToken ct = default);

    /// <summary>Marque une alerte ouverte ou reportée comme traitée. Faux si elle n'existe pas, est déjà close, ou n'est pas d'un des types donnés.</summary>
    Task<bool> TraiterAsync(long alerteId, string? actionPrise, long utilisateurId, IReadOnlyCollection<string> types, CancellationToken ct = default);
}

/// <summary>Types d'alerte sur lesquels l'employé peut agir ; sans service de droits : tous sauf ceux réservés au titulaire.</summary>
internal static class TypesActionnables
{
    public static async Task<IReadOnlyCollection<string>> LireAsync(Securite.IPermissionService? droits, long utilisateurId, CancellationToken ct) =>
        droits is not null ? await AlertesSelonDroits.TypesVisiblesAsync(droits, utilisateurId, ct)
            : AlertesSelonDroits.Tous.Where(t => !AlertesReservees.Types.Contains(t)).ToList();
}

/// <summary>Reporte une alerte jusqu'à la date choisie : elle réapparaît ce jour-là (generer_alertes).</summary>
public sealed class ReporterAlerte(IAlertesEcriture alertes, IHorloge horloge, Securite.IPermissionService? droits = null)
{
    public async Task<bool> ExecuterAsync(long alerteId, DateOnly jusquAu, long utilisateurId = 0, CancellationToken ct = default)
    {
        RegleAlerte.VerifierReport(jusquAu, horloge.Aujourdhui);
        return await alertes.ReporterAsync(alerteId, jusquAu, await TypesActionnables.LireAsync(droits, utilisateurId, ct), ct);
    }
}

/// <summary>
/// Clôt une alerte, avec l'action prise. Si la cause existe encore au prochain calcul,
/// une nouvelle alerte est créée (comportement de generer_alertes).
/// </summary>
public sealed class TraiterAlerte(IAlertesEcriture alertes, Securite.IPermissionService? droits = null)
{
    public const int LongueurMaxAction = 200;

    public async Task<bool> ExecuterAsync(long alerteId, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        var action = string.IsNullOrWhiteSpace(actionPrise) ? null : actionPrise.Trim();
        if (action is { Length: > LongueurMaxAction })
            action = action[..LongueurMaxAction];
        return await alertes.TraiterAsync(alerteId, action, utilisateurId, await TypesActionnables.LireAsync(droits, utilisateurId, ct), ct);
    }
}
