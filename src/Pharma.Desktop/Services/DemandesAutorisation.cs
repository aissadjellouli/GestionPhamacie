namespace Pharma.Desktop.Services;

/// <summary>
/// Action refusée faute de droit, que le serveur permet d'autoriser sur place : la mise en page ouvre la fenêtre « Faire autoriser »
/// (employé habilité et son code comptoir), puis l'appel est rejoué avec ce code. Une demande à la fois.
/// Seuil : remise refusée (pourcentage demandé) ; seuls les employés dont le maximum de remise la couvre sont proposés.
/// </summary>
public sealed class DemandesAutorisation
{
    /// <summary>Detail : tableau facultatif affiché au valideur sous le motif (ex. prix des lignes d'un volet Chifa, 2026-11-15).</summary>
    public sealed record Demande(string Message, string? Permission, TaskCompletionSource<(string Identifiant, string Code)?> Reponse, decimal? Seuil = null,
        Pharma.Shared.DetailAutorisationDto? Detail = null);

    public Demande? EnCours { get; private set; }
    public event Action? Changee;

    public Task<(string Identifiant, string Code)?> DemanderAsync(string message, string? permission, decimal? seuil = null,
        Pharma.Shared.DetailAutorisationDto? detail = null)
    {
        EnCours?.Reponse.TrySetResult(null);
        var tcs = new TaskCompletionSource<(string, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnCours = new Demande(message, permission, tcs, seuil, detail);
        Changee?.Invoke();
        return tcs.Task;
    }

    /// <summary>
    /// Abandonne la demande en cours si c'est celle-ci (écran appelant fermé) ; une demande ouverte depuis un autre écran
    /// n'est pas touchée.
    /// </summary>
    public void Abandonner(Task<(string Identifiant, string Code)?>? demande)
    {
        if (demande is not null && EnCours is { } d && ReferenceEquals(d.Reponse.Task, demande)) Repondre(null);
    }

    public void Repondre((string Identifiant, string Code)? reponse)
    {
        var d = EnCours;
        EnCours = null;
        Changee?.Invoke();
        d?.Reponse.TrySetResult(reponse);
    }
}
