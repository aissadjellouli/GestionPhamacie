namespace Pharma.Desktop.Services;

/// <summary>
/// Action refusée faute de droit, que le serveur permet d'autoriser sur place : la mise en page ouvre la fenêtre « Faire autoriser »
/// (employé habilité et son code comptoir), puis l'appel est rejoué avec ce code. Une demande à la fois.
/// </summary>
public sealed class DemandesAutorisation
{
    public sealed record Demande(string Message, string? Permission, TaskCompletionSource<(string Identifiant, string Code)?> Reponse);

    public Demande? EnCours { get; private set; }
    public event Action? Changee;

    public Task<(string Identifiant, string Code)?> DemanderAsync(string message, string? permission)
    {
        EnCours?.Reponse.TrySetResult(null);
        var tcs = new TaskCompletionSource<(string, string)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        EnCours = new Demande(message, permission, tcs);
        Changee?.Invoke();
        return tcs.Task;
    }

    public void Repondre((string Identifiant, string Code)? reponse)
    {
        var d = EnCours;
        EnCours = null;
        Changee?.Invoke();
        d?.Reponse.TrySetResult(reponse);
    }
}
