using System.Collections.Concurrent;

namespace Pharma.Cloud.Api.Reception;

/// <summary>
/// Débit pendant un chargement complet (script 006) : l'officine dont un chargement est annoncé et non terminé a une limite de dépôts
/// relevée (Cloud:LotsParMinuteChargement, 600/min par défaut au lieu de Cloud:LotsParMinute, 60). L'état est lu en base
/// (cloud_chargement_en_cours) avant la limite de débit et gardé quelques secondes par certificat ; le début et la fin d'un chargement
/// l'effacent aussitôt.
/// </summary>
public sealed class SuiviChargementCentral(Func<string, CancellationToken, Task<bool>> lire, TimeProvider temps)
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (bool EnCours, DateTimeOffset Lu)> etats = new(StringComparer.Ordinal);

    public async Task<bool> EnCoursAsync(string empreinteCertificat, CancellationToken ct)
    {
        var maintenant = temps.GetUtcNow();
        if (etats.TryGetValue(empreinteCertificat, out var e) && maintenant - e.Lu < Duree) return e.EnCours;
        var v = await lire(empreinteCertificat, ct);
        etats[empreinteCertificat] = (v, maintenant);
        if (etats.Count > 100_000) etats.Clear(); // borne mémoire (simple relecture ensuite)
        return v;
    }

    public void Oublier(string empreinteCertificat) => etats.TryRemove(empreinteCertificat, out _);

    /// <summary>Clé et limite de la politique « lots » : seau propre au chargement, limite relevée.</summary>
    public static (string Cle, int Limite) Politique(string cle, bool chargement, int normal, int pendantChargement) =>
        chargement ? (cle + "|chargement", pendantChargement) : (cle, normal);
}
