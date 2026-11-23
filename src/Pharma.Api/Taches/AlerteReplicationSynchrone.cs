using Pharma.Application.Notifications;
using Pharma.Infrastructure.SecondServeur;

namespace Pharma.Api.Taches;

/// <summary>
/// Vague 2, lot V2-7 : quand l'attente de la réplication synchrone est abandonnée (2e serveur décroché, vente validée quand même),
/// le titulaire est alerté, au plus une fois par heure. Aucune donnée de vente dans le message.
/// </summary>
public sealed class AlerteReplicationSynchrone(IServiceScopeFactory scopes, ILogger<AlerteReplicationSynchrone> journal) : IHostedService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromHours(1);
    public const string Objet = "Le 2e serveur ne suit plus les ventes en temps réel";
    public const string Detail = "Une vente a été validée sans attendre la copie sur le 2e serveur (attente de plus de 2 secondes abandonnée). "
        + "La vente est bien enregistrée sur ce serveur ; si le principal tombait maintenant, les dernières ventes pourraient manquer sur le 2e serveur. "
        + "Vérifiez que le 2e serveur est allumé et connecté.";
    public const string Sms = "Le 2e serveur ne suit plus les ventes en temps reel : verifiez qu'il est allume et connecte.";

    private long _derniere = long.MinValue;

    /// <summary>Vrai (et mémorise l'instant) si la dernière alerte date de plus d'une heure. Public pour les tests.</summary>
    public bool AlerteDue(long maintenantMs)
    {
        lock (this)
        {
            if (_derniere != long.MinValue && maintenantMs - _derniere < (long)Intervalle.TotalMilliseconds) return false;
            _derniere = maintenantMs;
            return true;
        }
    }

    public Task StartAsync(CancellationToken ct)
    {
        ReplicationSynchrone.AttenteAbandonnee += Surabandon;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct)
    {
        ReplicationSynchrone.AttenteAbandonnee -= Surabandon;
        return Task.CompletedTask;
    }

    private void Surabandon(TimeSpan duree)
    {
        if (!AlerteDue(Environment.TickCount64)) return;
        journal.LogWarning("Attente de la réplication synchrone abandonnée après {Duree} ms : titulaire prévenu", (long)duree.TotalMilliseconds);
        _ = EnvoyerAsync();
    }

    private async Task EnvoyerAsync()
    {
        try
        {
            using var scope = scopes.CreateScope();
            await AlerteNonEnvoyeeSignalee.SansBlocageAsync(
                () => scope.ServiceProvider.GetRequiredService<IAlertesTitulaire>().SecondServeurAsync(Objet, Detail, Sms, CancellationToken.None),
                "réplication synchrone abandonnée", scope.ServiceProvider.GetService<Pharma.Application.Commun.ITraceErreurs>());
        }
        catch (Exception e)
        {
            journal.LogError(e, "Alerte de réplication synchrone impossible");
        }
    }
}
