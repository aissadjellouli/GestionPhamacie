using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;

namespace Pharma.Application.Synchronisation;

// ---- Barre d'état du poste : liaison avec le serveur central et avec le 2e serveur (2026-11-21) ----
// Un élément par liaison, seulement en anomalie : rien si la liaison n'est pas en place (synchronisation non activée ou sans
// adresse, aucun 2e serveur déclaré), rien quand tout va bien. Lecture seule, sans donnée personnelle ni réglage technique
// (ni adresse, ni code d'officine, ni empreinte, ni message d'erreur) : vue par tous les employés connectés.

public static class NiveauxLiaison
{
    /// <summary>Retard ou action à prévoir (classe .attention de la barre d'état).</summary>
    public const string Attention = "attention";
    /// <summary>Envoi arrêté ou refusé, liaison coupée (classe .anormal de la barre d'état).</summary>
    public const string Anormal = "anormal";
}

/// <summary>Anomalie d'une liaison : libellé court (barre d'état), niveau (<see cref="NiveauxLiaison"/>), détail (infobulle).</summary>
public sealed record AnomalieLiaison(string Libelle, string Niveau, string Detail);

/// <summary>État des deux liaisons vu par un poste. Central / Second : null quand rien n'est à signaler.</summary>
public sealed record EtatLiaisonsPoste(bool CentralActive, AnomalieLiaison? Central, long LotsEnAttente, DateTime? DernierEnvoiLe,
    bool SecondConfigure, AnomalieLiaison? Second)
{
    public static readonly EtatLiaisonsPoste Rien = new(false, null, 0, null, false, null);
}

/// <summary>Règles d'affichage, sans accès à la base (testées).</summary>
public static class RegleLiaisonsPoste
{
    /// <summary>
    /// Serveur central, du plus grave au moins grave ; null : rien à afficher. Seuils : ceux de la supervision (Administration ›
    /// Synchronisation › Supervision : bandeau, alerte de retard, erreur persistante) et le délai de 30 jours du certificat.
    /// </summary>
    public static AnomalieLiaison? Central(ParametresEnvoiSync p, EtatSupervisionSync s, CertificatSyncInfo? cert, DateTime maintenantUtc)
    {
        // Pas reliée au central, ou 2e serveur en veille (base en lecture seule : il n'envoie rien) : rien
        if (!p.Active || string.IsNullOrWhiteSpace(p.Adresse) || p.BaseEnLectureSeule || s.BaseEnLectureSeule) return null;
        if (p.InscriteLe is null || cert is null)
            return new("Central : activation à faire", NiveauxLiaison.Attention,
                "Synchronisation activée mais ce serveur n’est pas encore activé auprès du serveur central : rien n’est envoyé.");
        if (s.RetourArriere is not null)
            return new("Central : envoi suspendu", NiveauxLiaison.Anormal,
                "Envoi au serveur central suspendu : base de l’officine revenue en arrière (restauration ou bascule). Le travail n’est pas affecté.");
        if (cert.EcheanceUtc <= maintenantUtc)
            return new("Central : certificat échu", NiveauxLiaison.Anormal,
                $"Certificat de l’officine échu depuis le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy} : plus rien n’est envoyé (nouveau code d’activation).");
        if (s.ErreurNature == NaturesErreurSync.Quota)
            return new("Central : quota refusé", NiveauxLiaison.Anormal,
                "Le serveur central refuse les envois (quota ou plafond atteint). Les opérations restent en attente, rien n’est perdu.");
        var attente = s.Attente is { Depuis: { } d, Operations: > 0 } ? maintenantUtc - d : (TimeSpan?)null;
        var enAttente = $"{s.Attente.Operations} opération(s) en attente ({p.LotsEnAttente} lot(s) non accusé(s))";
        if (s is { ErreurNature: NaturesErreurSync.Autre, ErreurDepuisLe: { } erreurDepuis }
            && maintenantUtc - erreurDepuis >= TimeSpan.FromMinutes(s.Reglages.ErreurMinutes))
            return new("Central : erreur d’envoi", NiveauxLiaison.Anormal,
                $"L’envoi au serveur central échoue depuis {SurveillerSynchronisation.Duree(maintenantUtc - erreurDepuis)}. {enAttente}.");
        if (attente is { } a1 && a1 >= TimeSpan.FromHours(s.Reglages.RetardHeures))
            return new($"Central : en retard ({SurveillerSynchronisation.Duree(a1)})", NiveauxLiaison.Anormal,
                $"{enAttente} depuis {SurveillerSynchronisation.Duree(a1)} (seuil d’alerte : {s.Reglages.RetardHeures} h).");
        if (cert.EcheanceUtc - maintenantUtc < SurveillerSynchronisation.DelaiCertificat)
            return new("Central : activation à refaire", NiveauxLiaison.Attention,
                $"Certificat de l’officine à échéance le {cert.EcheanceUtc.ToLocalTime():dd/MM/yyyy} : son renouvellement automatique n’a pas abouti (nouveau code d’activation).");
        if (attente is { } a2 && a2 >= TimeSpan.FromMinutes(s.Reglages.BandeauMinutes))
            return new($"Central : en attente ({SurveillerSynchronisation.Duree(a2)})", NiveauxLiaison.Attention,
                $"{enAttente} (seuil : {s.Reglages.BandeauMinutes} min). Le travail n’est pas affecté.");
        return null;
    }

    /// <summary>2e serveur : mêmes constats que l'alerte de réplication (<see cref="ReplicationOfficine.Probleme"/>) ; null : rien.</summary>
    public static AnomalieLiaison? Second(IReadOnlyList<ServeurOfficine> serveurs, EtatReplication e, ReglagesReplication r)
    {
        if (!ReplicationOfficine.Configuree(serveurs, e) || e.EnRecovery || ReplicationOfficine.Probleme(e, r) is not { } probleme) return null;
        if (!e.Connecte) return new("2e serveur : décroché", NiveauxLiaison.Anormal, probleme);
        if (e.RetardSecondes is { } s && s > r.SeuilSecondes)
            return new($"2e serveur : en retard ({SurveillerSynchronisation.Duree(TimeSpan.FromSeconds(s))})", NiveauxLiaison.Attention, probleme);
        return new($"2e serveur : en retard ({(e.RetardOctets ?? 0) / 1024 / 1024} Mo)", NiveauxLiaison.Attention, probleme);
    }
}

/// <summary>Mémoire de 30 secondes de l'état des liaisons (identique pour tous les postes) : chaque poste le relit toutes les minutes
/// et à chaque changement d'écran.</summary>
public sealed class MemoireLiaisonsPoste(IHorloge horloge)
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(30);
    private readonly object verrou = new();
    private (DateTime Quand, EtatLiaisonsPoste Etat)? gardee;

    public bool Lire(out EtatLiaisonsPoste etat)
    {
        lock (verrou)
            if (gardee is { } g && horloge.MaintenantUtc - g.Quand < Duree && horloge.MaintenantUtc >= g.Quand) { etat = g.Etat; return true; }
        etat = EtatLiaisonsPoste.Rien;
        return false;
    }

    public void Garder(EtatLiaisonsPoste etat) { lock (verrou) gardee = (horloge.MaintenantUtc, etat); }
}

/// <summary>
/// Lecture de l'état des liaisons pour la barre d'état (tout employé connecté). Chaque liaison est lue à part : une erreur de lecture
/// (réglage absent, droit pg_monitor manquant…) ne montre rien pour cette liaison, sans rien bloquer.
/// </summary>
public sealed class ConsulterLiaisonsPoste(IEnvoiSyncDepot envoi, ISupervisionSyncDepot supervision, MemoireSupervisionSync memoireSupervision,
    ICertificatSync certificat, ISecondServeurDepot second, MemoireLiaisonsPoste memoire, IHorloge horloge, ITraceErreurs? trace = null)
{
    public async Task<EtatLiaisonsPoste> ExecuterAsync(CancellationToken ct = default)
    {
        if (memoire.Lire(out var gardee)) return gardee;
        var maintenant = horloge.MaintenantUtc;
        (bool Active, AnomalieLiaison? Anomalie, long Lots, DateTime? Envoi) central = (false, null, 0, null);
        try
        {
            var p = await envoi.ParametresAsync(ct);
            var s = await memoireSupervision.LireAsync(supervision, ct);
            var active = p.Active && !string.IsNullOrWhiteSpace(p.Adresse);
            central = (active, active ? RegleLiaisonsPoste.Central(p, s, LireCertificat(), maintenant) : null, active ? p.LotsEnAttente : 0,
                active ? p.DernierEnvoiLe : null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("État du serveur central illisible pour la barre d'état", e); }

        (bool Configure, AnomalieLiaison? Anomalie) deuxieme = (false, null);
        try
        {
            var serveurs = await second.ServeursAsync(ct);
            var e = await second.ReplicationAsync(ReplicationOfficine.Slot, ct);
            var configure = ReplicationOfficine.Configuree(serveurs, e) && !e.EnRecovery;
            deuxieme = (configure, configure ? RegleLiaisonsPoste.Second(serveurs, e, await second.ReglagesAsync(ct)) : null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { trace?.Erreur("État du 2e serveur illisible pour la barre d'état", e); }

        var etat = new EtatLiaisonsPoste(central.Active, central.Anomalie, central.Lots, central.Envoi, deuxieme.Configure, deuxieme.Anomalie);
        memoire.Garder(etat);
        return etat;
    }

    private CertificatSyncInfo? LireCertificat()
    {
        try { return certificat.Lire(); }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or System.Security.Cryptography.CryptographicException)
        {
            trace?.Erreur("Certificat de synchronisation illisible pour la barre d'état", ex);
            return null;
        }
    }
}
