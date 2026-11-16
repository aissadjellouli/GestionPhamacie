using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using System.Security.Cryptography.X509Certificates;

namespace Pharma.Application.Synchronisation;

// ---- Synchronisation, lot S2 : envoi des lots au serveur central, accusés, activation (docs/conception-synchronisation.md § 12) ----
// Rien ne part tant que la synchronisation est désactivée (sync_parametre.active), que l'adresse du serveur central n'est pas
// réglée ou que ce serveur n'est pas activé (certificat client). Un lot n'est retiré de l'attente qu'après accusé du serveur
// central ; il est renvoyé à l'identique (même empreinte) après une coupure : le serveur central l'accuse sans le réappliquer.
// Un seul passage à la fois (VerrouSync) : tâche de fond, « Synchroniser maintenant », activation et renouvellement.

/// <summary>Réglages et état d'envoi (sync_parametre, scripts 126 à 128) et taille de l'attente.</summary>
/// <param name="BaseEnLectureSeule">pg_is_in_recovery() : 2e serveur en veille, rien n'est envoyé.</param>
/// <param name="LotsAutresEpoques">Lots non accusés d'une époque précédente (signalés, jamais envoyés automatiquement).</param>
public sealed record ParametresEnvoiSync(bool Active, Guid? OfficineId, int Epoque, string VersionContrat, long DernierLot, long DernierAccuse,
    string? Adresse, DateTime? InscriteLe, DateTime? DernierEnvoiLe, DateTime? DernierAccuseLe, string? DerniereErreur, DateTime? DerniereErreurLe,
    string TextesLibres, bool EnvoyerMedecins, long LignesEnAttente, long LotsEnAttente, bool BaseEnLectureSeule = false, long LotsAutresEpoques = 0);

/// <summary>Lot constitué (accusé ou non).</summary>
public sealed record LotEnregistre(int Epoque, long Numero, string VersionContrat, string Empreinte);

/// <summary>Certificat client de ce serveur (fichiers hors base : chaque serveur de l'officine a le sien).</summary>
public sealed record CertificatSyncInfo(string Empreinte, DateTime EcheanceUtc, string EmpreinteAutorite);

public interface IEnvoiSyncDepot
{
    Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default);
    /// <summary>Lots non accusés de l'époque courante, par n° croissant.</summary>
    Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default);
    /// <summary>Lots de l'époque, accusés ou non, de n° compris entre les bornes (reprise après un trou côté central).</summary>
    Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default);
    /// <summary>Lignes rattachées à un lot, par n° croissant (pour le renvoyer à l'identique).</summary>
    Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, CancellationToken ct = default);
    Task EnregistrerAccuseAsync(int epoque, long numero, CancellationToken ct = default);
    Task NoterEnvoiAsync(string? erreur, CancellationToken ct = default);
    Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default);
    /// <summary>Adresse, options et activation dans UNE transaction.</summary>
    Task ReglerAsync(string? adresse, string textesLibres, bool envoyerMedecins, bool active, CancellationToken ct = default);
}

/// <summary>Clé privée et certificat client de ce serveur : clé générée ici, jamais envoyée, stockée hors dépôt et hors base.</summary>
public interface ICertificatSync
{
    CertificatSyncInfo? Lire();
    /// <summary>Nouvelle clé (en attente) et sa demande de certificat (CSR PEM).</summary>
    string NouvelleDemande();
    /// <summary>Installe le certificat signé (doit correspondre à la clé en attente) et l'autorité à épingler.</summary>
    void Installer(string certificatPem, string autoritePem, string? epinglage);
    /// <summary>Certificat client avec sa clé privée (null si ce serveur n'est pas activé).</summary>
    X509Certificate2? CertificatClient();
    /// <summary>Autorité épinglée du serveur central (null avant activation).</summary>
    X509Certificate2? Autorite();
}

/// <summary>Certificat délivré par le serveur central (activation ou renouvellement).</summary>
public sealed record ReponseActivationSync(Guid OfficineId, string Serveur, string CertificatPem, string AutoritePem, DateTime EcheanceUtc, string? Epinglage);

/// <summary>Accusé d'un lot (statuts : StatutsLotSync).</summary>
public sealed record AccuseLotSync(int Epoque, long Numero, string Statut, long DernierRecu);

public static class StatutsLotSync
{
    public const string Accepte = "accepte";
    public const string DejaRecu = "deja_recu";
    public const string Trou = "trou";
    public const string EmpreinteDifferente = "empreinte_differente";
}

/// <param name="Definitif">Refus du serveur central sur le contenu (400, 403, 409, 413, 422) : renvoyer le même lot ne changera rien.</param>
public sealed class ServeurCentralException(string message, int? codeHttp = null) : Exception(message)
{
    public int? CodeHttp { get; } = codeHttp;
    public bool Definitif => CodeHttp is 400 or 403 or 409 or 413 or 422;
}

/// <summary>Appels au serveur central (HTTPS, certificat client, autorité épinglée, jeton court).</summary>
public interface IClientServeurCentral
{
    /// <summary>Code mal formé : ServeurCentralException. La réponse porte l'épinglage lu dans le code.</summary>
    Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default);
    Task<AccuseLotSync> DeposerAsync(string adresse, byte[] lot, CancellationToken ct = default);
    Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default);
}

/// <summary>Un seul passage de synchronisation à la fois dans l'API (singleton).</summary>
public sealed class VerrouSync
{
    private readonly SemaphoreSlim verrou = new(1, 1);
    public bool Prendre() => verrou.Wait(0);
    public void Rendre() => verrou.Release();
}

/// <param name="Bloquant">Erreur qui ne se résoudra pas seule (contenu refusé, lots manquants) : nouvel essai espacé au maximum.</param>
public sealed record ResultatEnvoi(int LotsAccuses, string? Arret, string? Erreur, bool Bloquant = false)
{
    public bool EnErreur => Erreur is not null;
}

/// <summary>Un passage d'envoi : constitue les lots en attente puis envoie, dans l'ordre, chaque lot non accusé.</summary>
public sealed class EnvoyerLotsSync(IEnvoiSyncDepot depot, ConstitutionLotsSync constitution, IFormatLotSync format, ICertificatSync certificat,
    IClientServeurCentral client, VerrouSync verrou)
{
    public const string ArretDesactivee = "Synchronisation désactivée.";
    public const string ArretSansAdresse = "Adresse du serveur central non réglée.";
    public const string ArretNonActivee = "Ce serveur n'est pas encore activé auprès du serveur central (code d'activation).";
    public const string ArretLectureSeule = "Base en lecture seule (2e serveur en veille) : seul le serveur principal envoie.";
    public const string ArretEnCours = "Envoi déjà en cours.";
    public static readonly TimeSpan RenouvelerAvant = TimeSpan.FromDays(60);

    public async Task<ResultatEnvoi> ExecuterAsync(DateTime maintenantUtc, int maxLots = 20, CancellationToken ct = default)
    {
        if (!verrou.Prendre()) return new(0, ArretEnCours, null);
        try { return await PasserAsync(maintenantUtc, maxLots, ct); }
        finally { verrou.Rendre(); }
    }

    private async Task<ResultatEnvoi> PasserAsync(DateTime maintenantUtc, int maxLots, CancellationToken ct)
    {
        var p = await depot.ParametresAsync(ct);
        if (p.BaseEnLectureSeule) return new(0, ArretLectureSeule, null);
        if (!p.Active) return new(0, ArretDesactivee, null);
        if (string.IsNullOrWhiteSpace(p.Adresse)) return new(0, ArretSansAdresse, null);
        var cert = certificat.Lire();
        if (p.InscriteLe is null || cert is null || p.OfficineId is not { } officine) return new(0, ArretNonActivee, null);
        if (cert.EcheanceUtc <= maintenantUtc) return await ErreurAsync(0, "Certificat de synchronisation échu : nouveau code d'activation nécessaire.", true, ct);

        var accuses = 0;
        try
        {
            await constitution.ConstituerAsync(10, ct);
            foreach (var lot in await depot.LotsNonAccusesAsync(maxLots, ct))
            {
                var a = await DeposerAsync(p.Adresse, officine, lot, ct);
                if (a.Statut == StatutsLotSync.Trou && a.DernierRecu < lot.Numero - 1)
                {
                    // Le serveur central n'a pas des lots déjà accusés ici (restauration du central) : renvoi des lots manquants
                    var manquants = await depot.LotsEntreAsync(lot.Epoque, a.DernierRecu + 1, lot.Numero - 1, ct);
                    if (manquants.Count != lot.Numero - 1 - a.DernierRecu)
                        return await ErreurAsync(accuses, $"Le serveur central n'a reçu que jusqu'au lot {a.DernierRecu} de l'époque {lot.Epoque} et les lots suivants ne sont plus disponibles ici : "
                            + "reprise impossible automatiquement, prévenez l'éditeur (procédure « Synchronisation, lots manquants » de docs/mise-en-production.md).", true, ct);
                    foreach (var m in manquants)
                    {
                        var am = await DeposerAsync(p.Adresse, officine, m, ct);
                        if (am.Statut is not (StatutsLotSync.Accepte or StatutsLotSync.DejaRecu))
                            return await ErreurAsync(accuses, $"Reprise du lot {m.Epoque}/{m.Numero} refusée par le serveur central ({am.Statut}).", true, ct);
                    }
                    a = await DeposerAsync(p.Adresse, officine, lot, ct);
                }
                switch (a.Statut)
                {
                    case StatutsLotSync.Accepte or StatutsLotSync.DejaRecu:
                        await depot.EnregistrerAccuseAsync(lot.Epoque, lot.Numero, ct);
                        accuses++;
                        break;
                    case StatutsLotSync.Trou:
                        return await ErreurAsync(accuses, $"Le serveur central n'a reçu que jusqu'au lot {a.DernierRecu} de l'époque {lot.Epoque} (lot {lot.Numero} refusé) : à examiner.", true, ct);
                    default:
                        return await ErreurAsync(accuses, $"Lot {lot.Epoque}/{lot.Numero} refusé par le serveur central ({a.Statut}).", true, ct);
                }
            }
            if (cert.EcheanceUtc - maintenantUtc < RenouvelerAvant) await RenouvelerAsync(p.Adresse, ct);
            await depot.NoterEnvoiAsync(p.LotsAutresEpoques > 0
                ? $"{p.LotsAutresEpoques} lot(s) d'une époque précédente jamais accusé(s) : à examiner (non renvoyés automatiquement)." : null, ct);
            return new(accuses, null, null);
        }
        catch (LotIncoherentException e) { return await ErreurAsync(accuses, e.Message, true, ct); }
        catch (ServeurCentralException e) { return await ErreurAsync(accuses, e.Message, e.Definitif, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Message technique sans donnée : type d'erreur seulement pour les cas imprévus
            var message = e switch
            {
                HttpRequestException => "Serveur central injoignable : " + e.Message,
                TaskCanceledException => "Serveur central injoignable (délai dépassé).",
                _ => $"Erreur technique d'envoi ({e.GetType().Name}) : voir le journal du serveur."
            };
            return await ErreurAsync(accuses, message, false, ct);
        }
    }

    /// <summary>Dépose le lot reconstitué à l'identique ; l'accusé doit porter la même époque et le même n°.</summary>
    private async Task<AccuseLotSync> DeposerAsync(string adresse, Guid officine, LotEnregistre lot, CancellationToken ct)
    {
        var lignes = await depot.LignesDuLotAsync(lot.Epoque, lot.Numero, ct);
        var f = format.Formuler(new EnTeteLotSync(lot.VersionContrat, officine, lot.Epoque, lot.Numero), lignes);
        if (f.Empreinte != lot.Empreinte)
            throw new LotIncoherentException($"Lot {lot.Epoque}/{lot.Numero} : contenu différent de son empreinte enregistrée (envoi arrêté).");
        var a = await client.DeposerAsync(adresse, f.Contenu, ct);
        if (a.Epoque != lot.Epoque || a.Numero != lot.Numero)
            throw new LotIncoherentException($"Accusé incohérent du serveur central (lot {a.Epoque}/{a.Numero} pour {lot.Epoque}/{lot.Numero}) : rien n'est marqué accusé.");
        return a;
    }

    private async Task RenouvelerAsync(string adresse, CancellationToken ct)
    {
        var demande = certificat.NouvelleDemande();
        var r = await client.RenouvelerAsync(adresse, demande, ct);
        certificat.Installer(r.CertificatPem, r.AutoritePem, null);
    }

    private async Task<ResultatEnvoi> ErreurAsync(int accuses, string erreur, bool bloquant, CancellationToken ct)
    {
        await depot.NoterEnvoiAsync(erreur, ct);
        return new(accuses, null, erreur, bloquant);
    }

    private sealed class LotIncoherentException(string message) : Exception(message);
}

/// <summary>Activation de ce serveur : code à usage unique (24 h) -> certificat client de 3 ans ; inscrit l'officine.</summary>
public sealed class ActiverSync(IEnvoiSyncDepot depot, ICertificatSync certificat, IClientServeurCentral client, IJournal journal, VerrouSync verrou)
{
    public async Task<CertificatSyncInfo> ExecuterAsync(string code, long utilisateurId, CancellationToken ct = default)
    {
        if (!verrou.Prendre()) throw new RegleMetierException("Envoi de synchronisation en cours : réessayez dans un instant.");
        try { return await ActiverAsync(code, utilisateurId, ct); }
        finally { verrou.Rendre(); }
    }

    private async Task<CertificatSyncInfo> ActiverAsync(string code, long utilisateurId, CancellationToken ct)
    {
        var p = await depot.ParametresAsync(ct);
        if (p.BaseEnLectureSeule) throw new RegleMetierException("Base en lecture seule (2e serveur en veille) : activation impossible ici.");
        if (string.IsNullOrWhiteSpace(p.Adresse)) throw new RegleMetierException("Réglez d'abord l'adresse du serveur central.", "adresse");
        if (string.IsNullOrWhiteSpace(code)) throw new RegleMetierException("Saisissez le code d'activation.", "code");
        if (p.OfficineId is not { } officine) throw new RegleMetierException("Code de l'officine absent (script 128 à passer).");
        var demande = certificat.NouvelleDemande();
        ReponseActivationSync r;
        try { r = await client.ActiverAsync(p.Adresse, code.Trim(), demande, officine, ct); }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException) { throw new RegleMetierException($"Activation refusée : {e.Message}", "code"); }
        if (r.OfficineId != officine) throw new RegleMetierException("Le serveur central a répondu pour une autre officine : activation abandonnée.");
        certificat.Installer(r.CertificatPem, r.AutoritePem, r.Epinglage);
        await depot.EnregistrerInscriptionAsync(officine, ct);
        await journal.EcrireAsync(utilisateurId, "synchronisation_activee", "sync_parametre", 1,
            new Dictionary<string, object?> { ["serveur"] = r.Serveur, ["echeance"] = r.EcheanceUtc.ToString("yyyy-MM-dd") }, null, ct);
        return certificat.Lire() ?? throw new InvalidOperationException("Certificat installé illisible.");
    }
}

/// <summary>Réglages de l'écran Admin › Synchronisation.</summary>
public sealed record ReglagesSync(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins);

public sealed class ReglerSync(IEnvoiSyncDepot depot, IJournal journal)
{
    public static readonly IReadOnlySet<string> ModesTextes = new HashSet<string>(StringComparer.Ordinal) { "filtres", "retires" };

    public static bool AdresseValide(string? a) =>
        string.IsNullOrWhiteSpace(a) || (Uri.TryCreate(a.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(u.Query) && u.AbsolutePath == "/" && string.IsNullOrEmpty(u.UserInfo));

    public async Task ExecuterAsync(ReglagesSync r, long utilisateurId, CancellationToken ct = default)
    {
        if (!AdresseValide(r.Adresse)) throw new RegleMetierException("Adresse du serveur central : https://nom-du-serveur[:port] attendu.", "adresse");
        if (!ModesTextes.Contains(r.TextesLibres)) throw new RegleMetierException("Textes libres : « filtres » ou « retires ».", "textes_libres");
        var avant = await depot.ParametresAsync(ct);
        var adresse = string.IsNullOrWhiteSpace(r.Adresse) ? null : r.Adresse.Trim().TrimEnd('/');
        await depot.ReglerAsync(adresse, r.TextesLibres, r.EnvoyerMedecins, r.Active, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_synchronisation", "sync_parametre", 1, new Dictionary<string, object?>
        {
            ["active"] = $"{avant.Active} -> {r.Active}",
            ["adresse"] = $"{avant.Adresse ?? "-"} -> {adresse ?? "-"}",
            ["textes_libres"] = $"{avant.TextesLibres} -> {r.TextesLibres}",
            ["envoyer_medecins"] = $"{avant.EnvoyerMedecins} -> {r.EnvoyerMedecins}"
        }, null, ct);
    }
}
