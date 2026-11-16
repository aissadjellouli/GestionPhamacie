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
    string TextesLibres, bool EnvoyerMedecins, long LignesEnAttente, long LotsEnAttente, bool BaseEnLectureSeule = false, long LotsAutresEpoques = 0,
    IReadOnlyList<string>? FamillesEnvoyees = null, DateTime? FamillesLuesLe = null, long DescendantVersion = 0, long FamillesVersion = 0,
    IReadOnlyList<string>? ColonnesInterdites = null);

/// <summary>Élément descendant reçu du serveur central (contrat desc-v1) : table locale, clé, version, données JSON.</summary>
public sealed record ChangementDescendantSync(string Table, string Cle, long Version, string DonneesJson);

/// <summary>Éléments descendants reçus et version maximale annoncée par le serveur central.</summary>
public sealed record DescendantsRecus(IReadOnlyList<ChangementDescendantSync> Changements, long VersionMax)
{
    public static readonly DescendantsRecus Aucun = new([], 0);
}

/// <summary>Statuts de sync_recevoir_descendant (script 136).</summary>
public static class StatutsDescendant
{
    public const string Applique = "applique";
    public const string Ancien = "ancien";
    public const string TableInconnue = "table_inconnue";
    public const string Refuse = "refuse";
}

/// <summary>Lot constitué (accusé ou non).</summary>
/// <param name="ColonnesRetirees">Colonnes (« table.colonne ») retirées à sa constitution, figées (script 136) ; pour un lot antérieur,
/// reconstituées depuis ses familles et le catalogue actuel.</param>
/// <param name="ColonnesFigees">false : lot antérieur au script 136, liste reconstituée (non garantie).</param>
/// <param name="Accuse">Déjà accusé par le serveur central (jamais reformé en base).</param>
/// <param name="ReformeLe">Lot déjà reformé (un 409 « autre contenu » veut alors dire que le central a la forme d'origine).</param>
public sealed record LotEnregistre(int Epoque, long Numero, string VersionContrat, string Empreinte, IReadOnlyList<string>? ColonnesRetirees = null,
    bool ColonnesFigees = true, bool Accuse = false, DateTime? ReformeLe = null);

/// <summary>Certificat client de ce serveur (fichiers hors base : chaque serveur de l'officine a le sien).</summary>
public sealed record CertificatSyncInfo(string Empreinte, DateTime EcheanceUtc, string EmpreinteAutorite);

public interface IEnvoiSyncDepot
{
    Task<ParametresEnvoiSync> ParametresAsync(CancellationToken ct = default);
    /// <summary>Lots non accusés de l'époque courante, par n° croissant.</summary>
    Task<IReadOnlyList<LotEnregistre>> LotsNonAccusesAsync(int maximum, CancellationToken ct = default);
    /// <summary>Lots de l'époque, accusés ou non, de n° compris entre les bornes (reprise après un trou côté central).</summary>
    Task<IReadOnlyList<LotEnregistre>> LotsEntreAsync(int epoque, long du, long au, CancellationToken ct = default);
    /// <summary>Lignes rattachées à un lot, par n° croissant, sans les colonnes listées (« table.colonne » ; null : aucun retrait).</summary>
    Task<IReadOnlyList<LigneSortieSync>> LignesDuLotAsync(int epoque, long numero, IReadOnlyList<string>? colonnesRetirees, CancellationToken ct = default);
    /// <summary>Éléments descendants reçus (sync_recevoir_descendant, script 136), dans une transaction : un statut par élément
    /// (StatutsDescendant), jamais d'exception pour un élément inconnu ou refusé ; la version acquittée n'est pas modifiée.</summary>
    Task<IReadOnlyList<string>> AppliquerDescendantsAsync(IReadOnlyList<ChangementDescendantSync> changements, long versionMax, CancellationToken ct = default);
    /// <summary>Version descendante acquittée (jamais en arrière).</summary>
    Task NoterVersionDescendanteAsync(long version, CancellationToken ct = default);
    /// <summary>Lot NON accusé reformé en retirant davantage de colonnes (sync_reformer_lot_colonnes).</summary>
    Task ReformerLotAsync(int epoque, long numero, string empreinte, int octets, IReadOnlyList<string> colonnesRetirees, CancellationToken ct = default);
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
public sealed record AccuseLotSync(int Epoque, long Numero, string Statut, long DernierRecu, DescendantsRecus? Descendants = null);

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
    /// <param name="versionDescendante">Plus grande version descendante appliquée ici (acquittement) ; l'accusé porte les éléments plus récents.</param>
    Task<AccuseLotSync> DeposerAsync(string adresse, byte[] lot, long versionDescendante, CancellationToken ct = default);
    Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default);
    /// <summary>Échange léger (chaque passage, même sans lot) : éléments descendants plus récents que la version appliquée.</summary>
    Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default);
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
/// <param name="chargement">Chargement complet (lot S6) ; null : aucun (tests du lot S2).</param>
public sealed class EnvoyerLotsSync(IEnvoiSyncDepot depot, ConstitutionLotsSync constitution, IFormatLotSync format, ICertificatSync certificat,
    IClientServeurCentral client, VerrouSync verrou, ChargementCompletSync? chargement = null)
{
    public const string ArretDesactivee = "Synchronisation désactivée.";
    public const string ArretSansAdresse = "Adresse du serveur central non réglée.";
    public const string ArretNonActivee = "Ce serveur n'est pas encore activé auprès du serveur central (code d'activation).";
    public const string ArretLectureSeule = "Base en lecture seule (2e serveur en veille) : seul le serveur principal envoie.";
    public const string ArretEnCours = "Envoi déjà en cours (passage automatique ou autre poste) : réessayez dans un instant.";
    private long versionDescendante;
    private IReadOnlyList<string> interdites = [];
    private string? erreurDescendante;
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
            // Voie descendante (script 131) : échange léger à chaque passage, même sans rien à envoyer, AVANT de former ou d'envoyer
            // un lot ; les réglages reçus sont écrits dans la base locale, seule source lue ensuite (central en panne : dernière
            // version appliquée ; rien jamais reçu : rien de nominatif)
            (versionDescendante, erreurDescendante) = (p.DescendantVersion, null);
            await EchangerDescendantsAsync(p.Adresse, ct);
            interdites = (await depot.ParametresAsync(ct)).ColonnesInterdites ?? [];
            // Chargement complet demandé : instantané puis annonce, AVANT de former les lots (lot S6)
            if (chargement is not null) await chargement.PreparerAsync(p.Adresse, p.DernierAccuse, ct);
            await constitution.ConstituerAsync(10, ct);
            foreach (var lot in await depot.LotsNonAccusesAsync(maxLots, ct))
            {
                var a = await DeposerAsync(p.Adresse, officine, lot, ct);
                if (a.Statut == StatutsLotSync.Trou && a.DernierRecu < lot.Numero - 1)
                {
                    // Le serveur central n'a pas des lots déjà accusés ici (restauration du central) : renvoi des lots manquants
                    var manquants = await depot.LotsEntreAsync(lot.Epoque, a.DernierRecu + 1, lot.Numero - 1, ct);
                    if (manquants.Count != lot.Numero - 1 - a.DernierRecu && chargement is not null && await chargement.DemanderPourLotsManquantsAsync(ct))
                        return await ErreurAsync(accuses, $"Le serveur central n'a reçu que jusqu'au lot {a.DernierRecu} de l'époque {lot.Epoque} et les lots suivants ne sont plus disponibles ici : "
                            + "rechargement complet de l'officine demandé (il part au prochain passage).", false, ct);
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
            if (chargement is not null) await chargement.TerminerSiAccuseAsync(p.Adresse, ct);
            if (cert.EcheanceUtc - maintenantUtc < RenouvelerAvant) await RenouvelerAsync(p.Adresse, ct);
            var note = string.Join(" ", new[] { p.LotsAutresEpoques > 0
                ? $"{p.LotsAutresEpoques} lot(s) d'une époque précédente jamais accusé(s) : à examiner (non renvoyés automatiquement)." : null, erreurDescendante }
                .Where(m => m is not null));
            await depot.NoterEnvoiAsync(note.Length > 0 ? note : null, ct);
            return new(accuses, null, null);
        }
        catch (LotIncoherentException e) { return await ErreurAsync(accuses, e.Message, true, ct); }
        catch (ChargementReporteException e) { return await ErreurAsync(accuses, e.Message, false, ct); }
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

    /// <summary>
    /// Dépose un lot. Il est d'abord reconstitué avec les colonnes retirées FIGÉES à sa constitution (script 136) et doit redonner
    /// son empreinte (sinon arrêt, sauf lot antérieur au script 136 dont la liste n'est que reconstituée). S'il faut retirer DAVANTAGE
    /// (famille décochée ou colonne ajoutée à une famille depuis), le contenu envoyé est recalculé en retirant aussi les colonnes
    /// interdites maintenant : une donnée non autorisée ne quitte jamais l'officine. Lot non accusé : reformé en base (réduction
    /// seulement). Lot déjà accusé renvoyé après un trou du central : contenu filtré envoyé SANS toucher la base (le central l'a perdu
    /// et l'accepte comme nouveau). Un lot reformé (ou au contenu recalculé) que le central a encore sous sa forme d'origine est refusé
    /// là-bas comme « autre contenu » (409) : il est tenu pour reçu (n° propre à l'officine, rien d'autre ne peut l'avoir envoyé).
    /// </summary>
    private async Task<AccuseLotSync> DeposerAsync(string adresse, Guid officine, LotEnregistre lot, CancellationToken ct)
    {
        var entete = new EnTeteLotSync(lot.VersionContrat, officine, lot.Epoque, lot.Numero);
        var reference = lot.ColonnesRetirees ?? [];
        var f = format.Formuler(entete, await depot.LignesDuLotAsync(lot.Epoque, lot.Numero, reference, ct));
        var reproduit = f.Empreinte == lot.Empreinte;
        if (!reproduit && lot.ColonnesFigees)
            throw new LotIncoherentException($"Lot {lot.Epoque}/{lot.Numero} : contenu différent de son empreinte enregistrée (envoi arrêté).");
        var autreForme = lot.ReformeLe is not null;
        if (!reproduit || interdites.Except(reference, StringComparer.Ordinal).Any())
        {
            var aRetirer = reference.Union(interdites, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var fr = format.Formuler(entete, await depot.LignesDuLotAsync(lot.Epoque, lot.Numero, aRetirer, ct));
            // Le contenu envoyé est TOUJOURS le contenu filtré (lot antérieur au script 136 : l'empreinte stockée peut être celle du filtré)
            if (fr.Empreinte != lot.Empreinte)
            {
                if (!lot.Accuse) await depot.ReformerLotAsync(lot.Epoque, lot.Numero, fr.Empreinte, fr.Contenu.Length, aRetirer, ct);
                autreForme = true;
            }
            f = fr;
        }
        AccuseLotSync a;
        try { a = await client.DeposerAsync(adresse, f.Contenu, versionDescendante, ct); }
        catch (ServeurCentralException e) when (autreForme && e.CodeHttp == 409)
        {
            return new AccuseLotSync(lot.Epoque, lot.Numero, StatutsLotSync.DejaRecu, lot.Numero);
        }
        if (a.Epoque != lot.Epoque || a.Numero != lot.Numero)
            throw new LotIncoherentException($"Accusé incohérent du serveur central (lot {a.Epoque}/{a.Numero} pour {lot.Epoque}/{lot.Numero}) : rien n'est marqué accusé.");
        if (a.Descendants is { } d) await AppliquerDescendantsAsync(d, ct);
        return autreForme && a.Statut == StatutsLotSync.EmpreinteDifferente ? a with { Statut = StatutsLotSync.DejaRecu } : a;
    }

    /// <summary>Échange léger : éléments descendants plus récents que la version appliquée ; échec : rien ne change ici.</summary>
    private async Task EchangerDescendantsAsync(string adresse, CancellationToken ct)
    {
        DescendantsRecus d;
        try { d = await client.EchangerAsync(adresse, versionDescendante, ct); }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested)) { return; }
        await AppliquerDescendantsAsync(d, ct);
    }

    /// <summary>
    /// Écrit les éléments reçus dans la base locale (jamais une version plus ancienne). La version acquittée n'avance que jusqu'avant
    /// le premier élément ignoré (table non prévue) ou refusé : il sera redonné, et rien n'est sauté. Une erreur d'application
    /// n'arrête pas le passage : les lots partent quand même (avec le dernier réglage appliqué) et l'erreur est notée.
    /// </summary>
    private async Task AppliquerDescendantsAsync(DescendantsRecus d, CancellationToken ct)
    {
        if (d.Changements.Count == 0) return;
        try
        {
            var statuts = await depot.AppliquerDescendantsAsync(d.Changements, d.VersionMax, ct);
            var bloquants = d.Changements.Zip(statuts).Where(x => x.Second is StatutsDescendant.TableInconnue or StatutsDescendant.Refuse)
                .Select(x => x.First.Version).ToList();
            var version = bloquants.Count > 0 ? bloquants.Min() - 1 : d.Changements.Max(c => c.Version);
            if (version > versionDescendante) await depot.NoterVersionDescendanteAsync(version, ct);
            var p = await depot.ParametresAsync(ct);
            (versionDescendante, interdites) = (p.DescendantVersion, p.ColonnesInterdites ?? []);
            if (bloquants.Count > 0)
                erreurDescendante = $"{bloquants.Count} élément(s) descendant(s) ignoré(s) (table non prévue ou refusé) : voir le journal sync_journal_descendant.";
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            erreurDescendante = $"Réglages du serveur central non appliqués ({e.GetType().Name}) : envoi poursuivi avec le dernier réglage appliqué.";
        }
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
/// <param name="chargements">Lot S6 : première activation = chargement complet demandé (null : aucun).</param>
public sealed class ActiverSync(IEnvoiSyncDepot depot, ICertificatSync certificat, IClientServeurCentral client, IJournal journal, VerrouSync verrou,
    IChargementSyncDepot? chargements = null)
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
        if (p.InscriteLe is null && chargements is not null) await chargements.DemanderAsync(MotifsChargement.Activation, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "synchronisation_activee", "sync_parametre", 1,
            new Dictionary<string, object?> { ["serveur"] = r.Serveur, ["echeance"] = r.EcheanceUtc.ToString("yyyy-MM-dd") }, null, ct);
        return certificat.Lire() ?? throw new InvalidOperationException("Certificat installé illisible.");
    }
}

/// <summary>Réglages de l'écran Admin › Synchronisation.</summary>
public sealed record ReglagesSync(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins);

/// <param name="chargements">Lot S6 : réactivation d'une officine inscrite = chargement complet (les modifications faites
/// pendant la désactivation n'ont pas été inscrites) ; null : aucun.</param>
public sealed class ReglerSync(IEnvoiSyncDepot depot, IJournal journal, IChargementSyncDepot? chargements = null)
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
        if (!avant.Active && r.Active && avant.InscriteLe is not null && chargements is not null)
            await chargements.DemanderAsync(MotifsChargement.Reactivation, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_synchronisation", "sync_parametre", 1, new Dictionary<string, object?>
        {
            ["active"] = $"{avant.Active} -> {r.Active}",
            ["adresse"] = $"{avant.Adresse ?? "-"} -> {adresse ?? "-"}",
            ["textes_libres"] = $"{avant.TextesLibres} -> {r.TextesLibres}",
            ["envoyer_medecins"] = $"{avant.EnvoyerMedecins} -> {r.EnvoyerMedecins}"
        }, null, ct);
    }
}
