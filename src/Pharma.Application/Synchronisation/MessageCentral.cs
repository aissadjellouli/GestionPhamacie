using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;

namespace Pharma.Application.Synchronisation;

// ---- Reste du lot S7.3 : message du titulaire à distance affiché sur les postes (docs/regles-metier.md « Barre d'état : message du
// titulaire », script local 160, script central 038) ----
// Le message arrive comme un ordre « message » (sync_ordre_recu), contrôlé et noté « appliqué » par ExecuterOrdreCentral. Il est
// montré à TOUS les employés connectés (pastille de la barre d'état) jusqu'à sa date de fin, tant que le titulaire de l'officine ne
// l'a pas fermé. Un message échu ou fermé n'est plus montré ; le plus récent remplace les précédents.

/// <summary>Message affiché : n° de l'ordre, texte (simple, 200 caractères au plus), fin d'affichage et réception (UTC).</summary>
public sealed record MessageCentral(Guid Id, string Texte, DateTime JusquAuUtc, DateTime RecuLeUtc);

/// <summary>Ordre « message » appliqué et pas encore fermé, tel que lu en base (données message-v1 brutes).</summary>
public sealed record MessageCentralLu(Guid Id, string DonneesJson, DateTime CreeLeUtc, DateTime RecuLeUtc);

public interface IMessageCentralDepot
{
    /// <summary>Le dernier message appliqué s'il n'est pas fermé (vide sinon : un message plus ancien n'est jamais rendu).</summary>
    Task<IReadOnlyList<MessageCentralLu>> AffichablesAsync(CancellationToken ct = default);

    /// <summary>Ferme un message affiché (sync_message_central_fermer : journal des actions) ; faux s'il n'était plus affichable.</summary>
    Task<bool> FermerAsync(Guid id, long par, CancellationToken ct = default);
}

/// <summary>Choix du message à afficher (règle pure, testée sans base).</summary>
public static class RegleMessageCentral
{
    /// <summary>
    /// Le plus récent message SEULEMENT (un nouveau message remplace le précédent : un ancien ne revient jamais quand le nouveau
    /// est échu ou fermé) : données relues (mêmes règles qu'à la réception), fin d'affichage pas encore passée à l'horloge de
    /// l'officine ; sinon null.
    /// </summary>
    public static MessageCentral? Choisir(IEnumerable<MessageCentralLu> lus, DateTime maintenantUtc)
    {
        if (lus.OrderByDescending(x => x.CreeLeUtc).FirstOrDefault() is not { } l) return null;
        var (texte, fin, refus) = MessagePostes.Lire(l.DonneesJson, l.CreeLeUtc);
        return refus is null && fin is { } f && f > maintenantUtc ? new MessageCentral(l.Id, texte!, f, l.RecuLeUtc) : null;
    }
}

/// <summary>Message à afficher (tout employé connecté) ; une lecture impossible n'affiche rien et ne bloque rien.</summary>
public sealed class LireMessageCentral(IMessageCentralDepot depot, IHorloge horloge, ITraceErreurs? trace = null)
{
    public async Task<MessageCentral?> ExecuterAsync(CancellationToken ct = default)
    {
        try
        {
            return RegleMessageCentral.Choisir(await depot.AffichablesAsync(ct), horloge.MaintenantUtc);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            trace?.Erreur("Message du serveur central illisible", e);
            return null;
        }
    }
}

/// <summary>Fermeture d'un message par le titulaire de l'officine : il disparaît de la barre d'état de tous les postes.</summary>
public sealed class FermerMessageCentral(IMessageCentralDepot depot, IPermissionService droits)
{
    public async Task ExecuterAsync(Guid id, long titulaireId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(titulaireId, "fermer un message du serveur central", ct);
        if (!await depot.FermerAsync(id, titulaireId, ct)) throw new RegleMetierException("Ce message n’est plus affiché (déjà fermé ou échu).");
    }
}
