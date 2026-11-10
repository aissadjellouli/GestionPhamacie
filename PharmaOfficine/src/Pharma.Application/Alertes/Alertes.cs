namespace Pharma.Application.Alertes;

public sealed record Alerte(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot, string? Emplacement = null);

public interface IAlertesLecture
{
    /// <summary>Alertes nouvelles ou vues, triées par urgence puis par montant en jeu.</summary>
    Task<IReadOnlyList<Alerte>> ListerOuvertesAsync(CancellationToken ct = default);
    /// <summary>Nombre d'alertes urgentes ouvertes (pastille du bandeau, lue à chaque navigation).</summary>
    Task<int> CompterUrgentesAsync(CancellationToken ct = default);
}

/// <summary>Calcul des alertes (aujourd'hui assuré par la fonction SQL generer_alertes).</summary>
public interface IMoteurAlertes
{
    Task<int> GenererAsync(CancellationToken ct = default);
}

public sealed class ListerAlertesOuvertes(IAlertesLecture alertes)
{
    public Task<IReadOnlyList<Alerte>> ExecuterAsync(CancellationToken ct = default) =>
        alertes.ListerOuvertesAsync(ct);

    public Task<int> CompterUrgentesAsync(CancellationToken ct = default) => alertes.CompterUrgentesAsync(ct);
}

/// <summary>Crée, met à jour ou clôt les alertes. Renvoie le nombre d'alertes créées ou mises à jour.</summary>
public sealed class GenererAlertes(IMoteurAlertes moteur)
{
    public Task<int> ExecuterAsync(CancellationToken ct = default) => moteur.GenererAsync(ct);
}
