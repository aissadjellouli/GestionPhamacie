using Pharma.Application.Caisse;
using Pharma.Application.Sauvegardes;

namespace Pharma.Application.Administration;

/// <summary>
/// État affiché dans la barre en bas du poste. Sauvegarde : la dernière (date de début, statut « reussie », « echouee » ou « disque_absent ») et si la sauvegarde
/// automatique est activée. Caisse : la session ouverte du poste, s'il y en a une.
/// </summary>
public sealed record EtatPoste(bool SauvegardeActive, DateTime? DerniereSauvegarde, string? StatutSauvegarde,
    long? CaisseSessionId, DateTime? CaisseOuverteLe);

/// <summary>Lecture légère pour tout employé connecté : rien de confidentiel (ni montant, ni fichier, ni message d'erreur).</summary>
public sealed class ObtenirEtatPoste(ISauvegardesDepot sauvegardes, ISessionsCaisse sessions)
{
    public async Task<EtatPoste> ExecuterAsync(string? poste, CancellationToken ct = default)
    {
        var reglages = await sauvegardes.ReglagesAsync(ct);
        var derniere = (await sauvegardes.JournalAsync(1, ct)).FirstOrDefault();
        var session = string.IsNullOrWhiteSpace(poste) ? null : await sessions.SessionDuPosteAsync(poste, ct);
        return new EtatPoste(reglages.Actif, derniere?.Debut, derniere?.Statut, session?.Id, session?.OuverteLe);
    }
}
