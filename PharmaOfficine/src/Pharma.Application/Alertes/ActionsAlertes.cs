using Pharma.Application.Commun;
using Pharma.Domain.Alertes;

namespace Pharma.Application.Alertes;

public interface IAlertesEcriture
{
    /// <summary>Reporte une alerte ouverte. Faux si elle n'existe pas ou n'est plus ouverte.</summary>
    Task<bool> ReporterAsync(long alerteId, DateOnly jusquAu, CancellationToken ct = default);

    /// <summary>Marque une alerte ouverte ou reportée comme traitée. Faux si elle n'existe pas ou est déjà close.</summary>
    Task<bool> TraiterAsync(long alerteId, string? actionPrise, long utilisateurId, CancellationToken ct = default);
}

/// <summary>Reporte une alerte jusqu'à la date choisie : elle réapparaît ce jour-là (generer_alertes).</summary>
public sealed class ReporterAlerte(IAlertesEcriture alertes, IHorloge horloge)
{
    public Task<bool> ExecuterAsync(long alerteId, DateOnly jusquAu, CancellationToken ct = default)
    {
        RegleAlerte.VerifierReport(jusquAu, horloge.Aujourdhui);
        return alertes.ReporterAsync(alerteId, jusquAu, ct);
    }
}

/// <summary>
/// Clôt une alerte, avec l'action prise. Si la cause existe encore au prochain calcul,
/// une nouvelle alerte est créée (comportement de generer_alertes).
/// </summary>
public sealed class TraiterAlerte(IAlertesEcriture alertes)
{
    public const int LongueurMaxAction = 200;

    public Task<bool> ExecuterAsync(long alerteId, string? actionPrise, long utilisateurId, CancellationToken ct = default)
    {
        var action = string.IsNullOrWhiteSpace(actionPrise) ? null : actionPrise.Trim();
        if (action is { Length: > LongueurMaxAction })
            action = action[..LongueurMaxAction];
        return alertes.TraiterAsync(alerteId, action, utilisateurId, ct);
    }
}
