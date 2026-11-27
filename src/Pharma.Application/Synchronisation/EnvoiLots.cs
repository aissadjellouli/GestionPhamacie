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
    IReadOnlyList<string>? ColonnesInterdites = null, DateTime? RetourArriereLe = null, long? RetourArriereCentral = null,
    DateTime? RetourArriereSequencesLe = null, int DelaiControleDemarrage = 120);

/// <summary>Élément descendant reçu du serveur central (contrat desc-v1) : table locale, clé, version, données JSON.</summary>
public sealed record ChangementDescendantSync(string Table, string Cle, long Version, string DonneesJson);

/// <summary>
/// Éléments descendants reçus et version maximale annoncée par le serveur central. Ecartes : éléments reçus mais écartés à la lecture
/// (mal formés ou trop gros) : jamais appliqués ni redonnés, signalés dans la note du passage. VersionEcarteeMax : plus grande version
/// parmi eux (0 : aucun), acquittée comme les autres pour qu'ils ne soient pas redonnés.
/// </summary>
public sealed record DescendantsRecus(IReadOnlyList<ChangementDescendantSync> Changements, long VersionMax, int Ecartes = 0, long VersionEcarteeMax = 0)
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
    /// <summary>Élément « mutu_ » d'une table non mutualisable (script 141) : journalisé et ignoré, acquitté (ne bloque pas les suivants).</summary>
    public const string Invalide = "invalide";
}

/// <summary>Lot constitué (accusé ou non).</summary>
/// <param name="ColonnesRetirees">Colonnes (« table.colonne ») retirées à sa constitution, figées (script 136) ; pour un lot antérieur,
/// reconstituées depuis ses familles et le catalogue actuel.</param>
/// <param name="ColonnesFigees">false : lot antérieur au script 136, liste reconstituée (non garantie).</param>
/// <param name="Accuse">Déjà accusé par le serveur central (jamais reformé en base).</param>
/// <param name="ReformeLe">Lot déjà reformé (un 409 « autre contenu » veut alors dire que le central a la forme d'origine).</param>
/// <param name="Purge">Lot accusé dont les lignes ont été retirées de la file après le délai de conservation (script 140) : il ne peut
/// plus être renvoyé, seul un chargement complet remplace son contenu au serveur central.</param>
public sealed record LotEnregistre(int Epoque, long Numero, string VersionContrat, string Empreinte, IReadOnlyList<string>? ColonnesRetirees = null,
    bool ColonnesFigees = true, bool Accuse = false, DateTime? ReformeLe = null, bool Purge = false);

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
    /// <summary>Résultat du passage pour la supervision (script 140) : null = passage sans erreur, sinon nature de l'erreur
    /// (<see cref="NaturesErreurSync"/>). Sert à dater le début d'une erreur persistante.</summary>
    Task NoterPassageAsync(string? natureErreur, CancellationToken ct = default) => Task.CompletedTask;
    Task EnregistrerInscriptionAsync(Guid officine, CancellationToken ct = default);
    /// <summary>Adresse, options et activation dans UNE transaction.</summary>
    Task ReglerAsync(string? adresse, string textesLibres, bool envoyerMedecins, bool active, CancellationToken ct = default);
    /// <summary>Lot S6.2 (script 149) : pose la détection « base revenue en arrière » si le serveur central a reçu plus de lots que cette
    /// base n'en a formé pour l'époque en cours (sync_noter_retour_arriere) ; true à la première détection seulement.</summary>
    Task<bool> NoterRetourArriereAsync(long dernierLotCentral, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Correction 2026-11-18 (script 149) : même détection, posée sans comparaison au dernier lot formé ici, quand un lot jamais
    /// reformé est refusé « empreinte différente » alors que le serveur central a reçu au moins ce n° (sync_noter_retour_arriere_refus).</summary>
    Task<bool> NoterRetourArriereRefusAsync(long dernierLotCentral, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S6.2 : avance (jamais en arrière) les séquences id des tables envoyées au-delà des n° connus du serveur central
    /// (sync_avancer_sequences) ; une ligne par table retenue.</summary>
    Task<IReadOnlyList<SequenceAvancee>> AvancerSequencesAsync(IReadOnlyDictionary<string, long> maximums, CancellationToken ct = default) =>
        throw new NotSupportedException();
    /// <summary>Lot S6.2 : nouvelle époque (sync_nouvelle_epoque), refusée en base sans détection ou si l'époque n'est plus celle attendue ;
    /// au-delà de l'époque locale et de <paramref name="epoqueCentral"/> (plus grande époque connue du serveur central, null : inconnue) ;
    /// chargement complet demandé. L'entrée du journal des actions (détails calculés depuis le résultat) est écrite dans la MÊME
    /// transaction.</summary>
    Task<NouvelleEpoqueSync> NouvelleEpoqueAsync(long titulaireId, int? epoqueCentral, int epoqueAttendue,
        Func<NouvelleEpoqueSync, IReadOnlyDictionary<string, object?>> journal, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S6.5.4 (script 151) : déclaration du changement d'époque pas encore envoyée (la plus récente) ; null : rien à déclarer.</summary>
    Task<DeclarationEpoqueSync?> DeclarationAEnvoyerAsync(CancellationToken ct = default) => Task.FromResult<DeclarationEpoqueSync?>(null);
    /// <summary>Lot S6.5.4 : résultat de l'envoi de la déclaration (envoyée, déjà reçue, route absente, refus), noté une fois.</summary>
    Task NoterDeclarationAsync(int nouvelleEpoque, bool? avantPremierLot, string resultat, CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Lot S6.5.4 (S6.5-D6 a) : compteur des tickets avancé au-delà du plus grand n° reçu par le central, plage notée
    /// (sync_avancer_compteur_ticket) ; rend le nombre de n° sautés (0 : rien à faire).</summary>
    Task<long> AvancerCompteurTicketAsync(long dernierTicketCentral, int epoque, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S6.5.4 (S6.5-D4, script 151) : prise d'acte du titulaire d'une restauration constatée (cause) et entrée du journal des
    /// actions dans UNE transaction ; déjà prise pour cette époque : RegleMetierException.</summary>
    Task PrendreActeRestaurationAsync(int nouvelleEpoque, string cause, long titulaireId, IReadOnlyDictionary<string, object?> journal,
        CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Script 152 : démarrage de l'API noté (sync_noter_demarrage) ; true : contrôle du serveur central attendu avant tout
    /// encaissement (synchronisation active, serveur activé).</summary>
    Task<bool> NoterDemarrageAsync(CancellationToken ct = default) => Task.FromResult(false);
    /// <summary>Script 152 : le serveur central a répondu au contrôle de démarrage (encaissement permis).</summary>
    Task NoterControleDemarrageAsync(CancellationToken ct = default) => Task.CompletedTask;
    /// <summary>Script 152 : secondes avant que l'encaissement soit permis (0 : permis).</summary>
    Task<int> AttenteEncaissementAsync(CancellationToken ct = default) => Task.FromResult(0);
    /// <summary>Script 152 : attente maximale au démarrage, en secondes (0 à 600).</summary>
    Task ReglerControleDemarrageAsync(int secondes, CancellationToken ct = default) => Task.CompletedTask;
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

/// <summary>Lot S6.5.4 : fin d'une chaîne de l'officine au changement d'époque (journal, clé, dernier rang, n°, empreinte hexadécimale).</summary>
public sealed record FinChaineSync(string Journal, string CleChaine, long Rang, string? Id, string? Hash);

/// <summary>Lot S6.5.4 (S6.5-D1) : déclaration du changement d'époque, envoyée au central avant tout lot de la nouvelle époque.</summary>
public sealed record DeclarationEpoqueSync(int AncienneEpoque, int NouvelleEpoque, long DernierLotLocal, long? DernierLotCentral,
    DateTime? DetecteLeUtc, DateTime DecideLeUtc, long? TitulaireId, IReadOnlyList<FinChaineSync> FinsChaine);

/// <summary>Réponse du central à la déclaration : déjà reçue (renvoi), arrivée avant tout lot de la nouvelle époque.</summary>
public sealed record ReponseDeclarationSync(bool DejaDeclaree, bool AvantPremierLot);

/// <summary>Certificat délivré par le serveur central (activation ou renouvellement).</summary>
public sealed record ReponseActivationSync(Guid OfficineId, string Serveur, string CertificatPem, string AutoritePem, DateTime EcheanceUtc, string? Epinglage);

/// <summary>Accusé d'un lot (statuts : StatutsLotSync).</summary>
public sealed record AccuseLotSync(int Epoque, long Numero, string Statut, long DernierRecu, DescendantsRecus? Descendants = null);

/// <summary>Nature de l'erreur du dernier passage (sync_parametre.erreur_nature).</summary>
public static class NaturesErreurSync
{
    public const string Autre = "autre";
    /// <summary>Quota ou plafond refusé par le serveur central (429 avec un motif).</summary>
    public const string Quota = "quota";
}

public static class StatutsLotSync
{
    public const string Accepte = "accepte";
    public const string DejaRecu = "deja_recu";
    public const string Trou = "trou";
    public const string EmpreinteDifferente = "empreinte_differente";
}

/// <param name="Definitif">Refus du serveur central sur le contenu (400, 403, 409, 413, 422) : renvoyer le même lot ne changera rien.</param>
/// <param name="quota">Refus pour quota ou plafond (429 accompagné d'un motif ; la limite de débit seule n'en porte pas).</param>
public sealed class ServeurCentralException(string message, int? codeHttp = null, bool quota = false) : Exception(message)
{
    public int? CodeHttp { get; } = codeHttp;
    public bool Quota { get; } = quota;
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
    /// <summary>Lot S6.2 : dernier lot de l'époque reçu par le serveur central (GET /api/v1/etat) ; null : réponse pour une autre époque
    /// (inexploitable). Sans implémentation : NotSupportedException (jamais « pas de contrôle » en silence).</summary>
    Task<long?> DernierLotCentralAsync(string adresse, int epoque, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S6.2 : plus grand id reçu par table de cette officine (GET /api/v1/maximums, aucune donnée de ligne).
    /// Route absente du serveur central : ServeurCentralException 404. Sans implémentation : NotSupportedException.</summary>
    Task<IReadOnlyDictionary<string, long>> MaximumsAsync(string adresse, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Correction 2026-11-18 : plus grande époque connue du serveur central pour cette officine (GET /api/v1/etat, champ
    /// facultatif EpoqueMax) ; null : serveur central sans ce renseignement. Sans implémentation : NotSupportedException.</summary>
    Task<int?> EpoqueMaxCentralAsync(string adresse, int epoque, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S6.5.4 : déclaration du changement d'époque (POST /api/v1/epoque). Route absente : ServeurCentralException 404 ;
    /// autre déclaration déjà reçue pour cette époque : 409. Sans implémentation : NotSupportedException.</summary>
    Task<ReponseDeclarationSync> DeclarerEpoqueAsync(string adresse, DeclarationEpoqueSync declaration, CancellationToken ct = default) =>
        throw new NotSupportedException();
    /// <summary>Lot S7.2b : code de rattachement de cette officine (celle du certificat) à un compte distant (POST /api/v1/rattachement).
    /// Refus neutre : ServeurCentralException 422 ; limite : 429. Sans implémentation : NotSupportedException.</summary>
    Task<CodeRattachementSync> CodeRattachementAsync(string adresse, string compte, CancellationToken ct = default) =>
        throw new NotSupportedException();
    /// <summary>Lot S7.3 : comptes distants rattachés à cette officine (GET /api/v1/acces-distants). Sans implémentation : NotSupportedException.</summary>
    Task<IReadOnlyList<CompteDistantSync>> ComptesDistantsAsync(string adresse, CancellationToken ct = default) => throw new NotSupportedException();
    /// <summary>Lot S7.3 : droits d'un compte rattaché (POST /api/v1/acces-distants/droits) ; rend le nombre d'ordres ouverts annulés.
    /// Compte non rattaché ou droits invalides : ServeurCentralException 422. Sans implémentation : NotSupportedException.</summary>
    Task<int> DroitsCompteDistantAsync(string adresse, string compte, IReadOnlyList<string> droits, CancellationToken ct = default) =>
        throw new NotSupportedException();
    /// <summary>Lot S7.3 : retrait d'un compte rattaché (POST /api/v1/acces-distants/retrait, effet immédiat) ; rend le nombre d'ordres
    /// ouverts annulés. Compte non rattaché : ServeurCentralException 422. Sans implémentation : NotSupportedException.</summary>
    Task<int> RetirerCompteDistantAsync(string adresse, string compte, CancellationToken ct = default) => throw new NotSupportedException();
}

/// <summary>Code de rattachement délivré par le serveur central (montré une fois, jamais gardé) et son échéance (UTC).</summary>
public sealed record CodeRattachementSync(string Code, DateTime ExpireLeUtc);

/// <summary>Lot S7.3 : compte distant rattaché à cette officine, vu du poste (droits : consulter, journal_acces, ordres).</summary>
public sealed record CompteDistantSync(string Identifiant, string Nom, IReadOnlyList<string> Droits, DateTime RattacheLeUtc, bool Actif);

/// <summary>Un seul passage de synchronisation à la fois dans l'API (singleton).</summary>
public sealed class VerrouSync
{
    private readonly SemaphoreSlim verrou = new(1, 1);
    public bool Prendre() => verrou.Wait(0);
    public void Rendre() => verrou.Release();
}

/// <param name="Bloquant">Erreur qui ne se résoudra pas seule (contenu refusé, lots manquants) : nouvel essai espacé au maximum.</param>
/// <param name="Suspendu">Envoi suspendu par un retour en arrière de la base (lot S6.2) : Erreur porte le message, ce n'est pas une
/// panne du serveur central (« Synchroniser maintenant » le montre comme un avertissement).</param>
public sealed record ResultatEnvoi(int LotsAccuses, string? Arret, string? Erreur, bool Bloquant = false, bool Suspendu = false)
{
    public bool EnErreur => Erreur is not null;
}

/// <summary>Un passage d'envoi : constitue les lots en attente puis envoie, dans l'ordre, chaque lot non accusé.</summary>
/// <param name="chargement">Chargement complet (lot S6) ; null : aucun (tests du lot S2).</param>
public sealed class EnvoyerLotsSync(IEnvoiSyncDepot depot, ConstitutionLotsSync constitution, IFormatLotSync format, ICertificatSync certificat,
    IClientServeurCentral client, VerrouSync verrou, ChargementCompletSync? chargement = null, Reglages.AppliquerReglagesMutualises? reglages = null)
{
    public const string ArretDesactivee = "Synchronisation désactivée.";
    public const string ArretSansAdresse = "Adresse du serveur central non réglée.";
    public const string ArretNonActivee = "Ce serveur n'est pas encore activé auprès du serveur central (code d'activation).";
    public const string ArretLectureSeule = "Base en lecture seule (2e serveur en veille) : seul le serveur principal envoie.";
    public const string ArretEnCours = "Envoi déjà en cours (passage automatique ou autre poste) : réessayez dans un instant.";
    private long versionDescendante;
    private IReadOnlyList<string> interdites = [];
    private string? erreurDescendante;
    private bool descendantsAAppliquer;
    public static readonly TimeSpan RenouvelerAvant = TimeSpan.FromDays(60);

    public async Task<ResultatEnvoi> ExecuterAsync(DateTime maintenantUtc, int maxLots = 20, CancellationToken ct = default)
    {
        if (!verrou.Prendre()) return new(0, ArretEnCours, null);
        try { return await PasserAsync(maintenantUtc, maxLots, ct); }
        finally { verrou.Rendre(); }
    }

    /// <summary>Table de la file des résultats d'ordres (script 145) : seule envoyée sans le pack G.</summary>
    public const string TableResultatsOrdres = "sync_ordre_resultat";

    /// <summary>
    /// Voie réduite sans le pack G (décision du titulaire du 2026-11-27, point 3) : la synchronisation est arrêtée, mais l'officine relève encore
    /// les éléments descendants (échange léger : ordres du central, dont l'activation de l'offre ; réglages rangés dans la base sans être appliqués)
    /// et, si <paramref name="envoyerResultats"/>, envoie les résultats d'ordres SEULS (lots formés des seules lignes « sync_ordre_resultat » ; les
    /// autres lignes restent en file pour le jour où le pack G revient). Aucun chargement complet, aucune déclaration d'époque, aucun contrôle de
    /// retour en arrière (rien n'est posé qui suspendrait l'encaissement), aucun renouvellement du certificat. Un lot refusé est une erreur simple,
    /// reprise au passage suivant. Un lot déjà formé avant la fin du pack (données autorisées alors) part avant, dans l'ordre des n°.
    /// </summary>
    public async Task<ResultatEnvoi> ExecuterSansPackAsync(DateTime maintenantUtc, bool envoyerResultats, CancellationToken ct = default)
    {
        if (!verrou.Prendre()) return new(0, ArretEnCours, null);
        try { return await PasserSansPackAsync(maintenantUtc, envoyerResultats, ct); }
        finally { verrou.Rendre(); }
    }

    private async Task<ResultatEnvoi> PasserSansPackAsync(DateTime maintenantUtc, bool envoyerResultats, CancellationToken ct)
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
            (versionDescendante, erreurDescendante) = (p.DescendantVersion, null);
            await EchangerDescendantsAsync(p.Adresse, ct);
            if (envoyerResultats)
            {
                interdites = (await depot.ParametresAsync(ct)).ColonnesInterdites ?? [];
                await constitution.ConstituerAsync(10, ct, [TableResultatsOrdres]);
                foreach (var lot in await depot.LotsNonAccusesAsync(20, ct))
                {
                    var a = await DeposerAsync(p.Adresse, officine, lot, ct);
                    if (a.Statut is not (StatutsLotSync.Accepte or StatutsLotSync.DejaRecu))
                        return await ErreurAsync(accuses, $"Lot {lot.Epoque}/{lot.Numero} des résultats d'ordres non accepté par le serveur central ({a.Statut}) : "
                            + "nouvel essai au prochain passage.", false, ct);
                    await depot.EnregistrerAccuseAsync(lot.Epoque, lot.Numero, ct);
                    accuses++;
                }
            }
            await depot.NoterEnvoiAsync(erreurDescendante, ct);
            await depot.NoterPassageAsync(null, ct);
            return new(accuses, null, null);
        }
        catch (LotIncoherentException e) { return await ErreurAsync(accuses, e.Message, true, ct); }
        catch (ServeurCentralException e) { return await ErreurAsync(accuses, e.Message, e.Definitif, ct, e.Quota); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var message = e switch
            {
                HttpRequestException => "Serveur central injoignable : " + e.Message,
                TaskCanceledException => "Serveur central injoignable (délai dépassé).",
                _ => $"Erreur technique d'envoi ({e.GetType().Name}) : voir le journal du serveur."
            };
            return await ErreurAsync(accuses, message, false, ct);
        }
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
            // Mutualisation des réglages (lot M4) : les modèles déjà reçus sont dans la base, même si l'échange vient d'échouer
            await AppliquerReglagesAsync(ct);
            interdites = (await depot.ParametresAsync(ct)).ColonnesInterdites ?? [];
            // Lot S6.2 : base revenue en arrière (restauration, bascule) ? Contrôlé AVANT de former, d'annoncer ou d'envoyer quoi que ce soit
            if (await RetourArriereAsync(p, ct) is { } suspendu) return suspendu;
            // Lot S6.5.4 : changement d'époque déclaré au central AVANT tout lot de la nouvelle époque (chargement compris)
            if (await DeclarerEpoqueAsync(p, ct) is { } arret) return arret;
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
                    // Lots purgés (délai de conservation dépassé, script 140) : leur contenu n'est plus ici, le chargement complet le remplace
                    var introuvables = manquants.Count != lot.Numero - 1 - a.DernierRecu || manquants.Any(m => m.Purge);
                    if (introuvables && chargement is not null && await chargement.DemanderPourLotsManquantsAsync(ct))
                        return await ErreurAsync(accuses, $"Le serveur central n'a reçu que jusqu'au lot {a.DernierRecu} de l'époque {lot.Epoque} et les lots suivants ne sont plus disponibles ici : "
                            + "rechargement complet de l'officine demandé (il part au prochain passage).", false, ct);
                    if (introuvables)
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
                    case StatutsLotSync.EmpreinteDifferente when lot.ReformeLe is null && a.DernierRecu >= lot.Numero:
                        // Lot jamais reformé ici, que le central a déjà reçu sous ce n° avec un autre contenu : la base est revenue en
                        // arrière (lots reformés depuis la restauration). Même suspension que la détection par /etat.
                        await depot.NoterRetourArriereRefusAsync(a.DernierRecu, ct);
                        return await SuspendreAsync(await depot.ParametresAsync(ct), accuses, a.DernierRecu, ct);
                    default:
                        return await ErreurAsync(accuses, $"Lot {lot.Epoque}/{lot.Numero} refusé par le serveur central ({a.Statut}).", true, ct);
                }
            }
            if (chargement is not null) await chargement.TerminerSiAccuseAsync(p.Adresse, ct);
            // Éléments reçus avec l'accusé d'un lot : appliqués dans le même passage
            if (descendantsAAppliquer) await AppliquerReglagesAsync(ct);
            // Renouvellement hors du sort des lots (revue du 2026-11-22) : un refus ou un quota n'est qu'une note, les lots accusés le restent
            var renouvellement = cert.EcheanceUtc - maintenantUtc < RenouvelerAvant ? await EssayerRenouvelerAsync(p.Adresse, cert.EcheanceUtc, ct) : null;
            var note = string.Join(" ", new[] { p.LotsAutresEpoques > 0
                ? $"{p.LotsAutresEpoques} lot(s) d'une époque précédente jamais accusé(s) : à examiner (non renvoyés automatiquement)." : null, erreurDescendante,
                renouvellement }
                .Where(m => m is not null));
            await depot.NoterEnvoiAsync(note.Length > 0 ? note : null, ct);
            await depot.NoterPassageAsync(null, ct);
            return new(accuses, null, null);
        }
        catch (LotIncoherentException e) { return await ErreurAsync(accuses, e.Message, true, ct); }
        catch (ChargementReporteException e) { return await ErreurAsync(accuses, e.Message, false, ct); }
        catch (ServeurCentralException e) { return await ErreurAsync(accuses, e.Message, e.Definitif, ct, e.Quota); }
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
        if (lot.Purge)
            throw new LotIncoherentException($"Lot {lot.Epoque}/{lot.Numero} : lignes purgées de la file après le délai de conservation, renvoi impossible (chargement complet nécessaire).");
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
        if (d.Ecartes > 0)
            erreurDescendante = $"{d.Ecartes} élément(s) descendant(s) écarté(s) à la réception (mal formés ou trop gros) : non appliqués, voir le journal du serveur.";
        if (d.Changements.Count == 0 && d.VersionEcarteeMax <= 0) return;
        descendantsAAppliquer = d.Changements.Count > 0;
        try
        {
            var statuts = d.Changements.Count > 0 ? await depot.AppliquerDescendantsAsync(d.Changements, d.VersionMax, ct) : [];
            var bloquants = d.Changements.Zip(statuts).Where(x => x.Second is StatutsDescendant.TableInconnue or StatutsDescendant.Refuse)
                .Select(x => x.First.Version).ToList();
            // Les éléments écartés comptent comme reçus (ils ne redeviendront jamais valables) ; seul un bloquant retient l'acquittement
            var recues = Math.Max(d.Changements.Count > 0 ? d.Changements.Max(c => c.Version) : 0, d.VersionEcarteeMax);
            var version = bloquants.Count > 0 ? bloquants.Min() - 1 : recues;
            if (version > versionDescendante) await depot.NoterVersionDescendanteAsync(version, ct);
            var p = await depot.ParametresAsync(ct);
            (versionDescendante, interdites) = (p.DescendantVersion, p.ColonnesInterdites ?? []);
            if (bloquants.Count > 0)
                erreurDescendante = (erreurDescendante is null ? "" : erreurDescendante + " ")
                    + $"{bloquants.Count} élément(s) descendant(s) ignoré(s) (table non prévue ou refusé) : voir le journal sync_journal_descendant.";
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            erreurDescendante = (erreurDescendante is null ? "" : erreurDescendante + " ")
                + $"Réglages du serveur central non appliqués ({e.GetType().Name}) : envoi poursuivi avec le dernier réglage appliqué.";
        }
    }

    /// <summary>Applique les modèles de réglages reçus (un échec ne fait jamais échouer l'envoi : noté, repris au passage suivant).</summary>
    private async Task AppliquerReglagesAsync(CancellationToken ct)
    {
        descendantsAAppliquer = false;
        if (reglages is null) return;
        try { await reglages.ExecuterAsync(ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            erreurDescendante = (erreurDescendante is null ? "" : erreurDescendante + " ")
                + $"Réglages mutualisés non appliqués ({e.GetType().Name}) : repris au prochain passage.";
        }
    }

    /// <summary>
    /// Lot S6.2 (conception § 30) : si le serveur central a reçu, pour l'époque en cours, un lot plus grand que le dernier lot formé ici,
    /// la base est revenue en arrière (restauration d'une sauvegarde, bascule sur le 2e serveur avec retard de réplication) : les lots
    /// suivants seraient refusés (autre contenu) et les nouveaux n° de lignes reprendraient des n° déjà reçus. La détection est notée
    /// une fois (l'alerte au titulaire part par la supervision), les séquences sont avancées au-delà des n° connus du central (nouvel
    /// essai à chaque passage tant que ce n'est pas fait) et rien n'est formé ni envoyé tant que le titulaire n'a pas repris sur une
    /// nouvelle époque. Correction 2026-11-18 : central injoignable ou réponse inexploitable pour /etat : rien n'est formé ni envoyé à
    /// ce passage (erreur non bloquante, nouvel essai au passage suivant) ; un lot ne part jamais sans ce contrôle.
    /// </summary>
    /// <returns>null : rien de détecté, le passage continue ; sinon le passage arrêté (suspension ou contrôle impossible).</returns>
    private async Task<ResultatEnvoi?> RetourArriereAsync(ParametresEnvoiSync p, CancellationToken ct)
    {
        var adresse = p.Adresse!;
        if (p.RetourArriereLe is null)
        {
            long? central;
            try { central = await client.DernierLotCentralAsync(adresse, p.Epoque, ct); }
            catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                return await ErreurAsync(0, MessageEtatInjoignable(e is TaskCanceledException ? "délai dépassé" : e.Message), false, ct);
            }
            if (central is not { } c) return await ErreurAsync(0, MessageEtatInjoignable("réponse pour une autre époque"), false, ct);
            if (c <= p.DernierLot)
            {
                // Script 152 : pas de retour en arrière, l'encaissement suspendu au démarrage est permis
                await depot.NoterControleDemarrageAsync(ct);
                return null;
            }
            await depot.NoterRetourArriereAsync(c, ct);
            p = await depot.ParametresAsync(ct);
            if (p.RetourArriereLe is null) { await depot.NoterControleDemarrageAsync(ct); return null; } // lot formé entre-temps : rien de posé
        }
        var suspendu = await SuspendreAsync(p, 0, null, ct);
        // Script 152 : retour en arrière, encaissement permis seulement une fois le compteur des tickets avancé (sinon : au bout du délai)
        if ((await depot.ParametresAsync(ct)).RetourArriereSequencesLe is not null) await depot.NoterControleDemarrageAsync(ct);
        return suspendu;
    }

    /// <summary>
    /// Lot S6.5.4 (S6.5-D1, conception § 31) : déclaration du changement d'époque notée par « Repartir sur une nouvelle époque »,
    /// envoyée au central avant tout lot de cette époque (le central ne reconnaît une restauration que si elle est arrivée avant).
    /// Central injoignable : rien n'est envoyé à ce passage (nouvel essai) ; route absente (404), autre déclaration déjà reçue (409) ou
    /// refus (400) : résultat noté, les lots partent (sans indulgence au contrôle d'intégrité). Époque dépassée : notée non envoyée.
    /// </summary>
    private async Task<ResultatEnvoi?> DeclarerEpoqueAsync(ParametresEnvoiSync p, CancellationToken ct)
    {
        var d = await depot.DeclarationAEnvoyerAsync(ct);
        if (d is null) return null;
        if (d.NouvelleEpoque != p.Epoque)
        {
            await depot.NoterDeclarationAsync(d.NouvelleEpoque, null, $"Non envoyée : époque {d.NouvelleEpoque} dépassée (époque en cours {p.Epoque}).", ct);
            return null;
        }
        try
        {
            var r = await client.DeclarerEpoqueAsync(p.Adresse!, d, ct);
            await depot.NoterDeclarationAsync(d.NouvelleEpoque, r.AvantPremierLot,
                (r.DejaDeclaree ? "Déjà reçue par le serveur central." : "Reçue par le serveur central.")
                + (r.AvantPremierLot ? "" : " Arrivée après un lot de la nouvelle époque : aucune indulgence au contrôle d'intégrité."), ct);
            return null;
        }
        catch (ServeurCentralException e) when (e.CodeHttp is 404)
        {
            await depot.NoterDeclarationAsync(d.NouvelleEpoque, null,
                "Non envoyée : le serveur central ne connaît pas la route de déclaration (scripts centraux 022 à 024) ; lots envoyés sans déclaration.", ct);
            return null;
        }
        catch (ServeurCentralException e) when (e.CodeHttp is 409 or 400)
        {
            await depot.NoterDeclarationAsync(d.NouvelleEpoque, null, $"Refusée par le serveur central ({e.Message}) ; lots envoyés sans cette déclaration.", ct);
            return null;
        }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return await ErreurAsync(0, $"Serveur central injoignable pour la déclaration de l'époque {d.NouvelleEpoque} "
                + $"({(e is TaskCanceledException ? "délai dépassé" : e.Message)}) : rien n'est envoyé à ce passage, nouvel essai au prochain passage.", false, ct);
        }
    }

    public static string MessageEtatInjoignable(string detail) =>
        $"Serveur central injoignable pour le contrôle de l'état ({detail}) : rien n'est formé ni envoyé à ce passage, nouvel essai au prochain passage.";

    /// <summary>Suspension (détection posée) : avance des n° si elle n'est pas faite, message clair gardé tant que la détection dure.</summary>
    private async Task<ResultatEnvoi> SuspendreAsync(ParametresEnvoiSync p, int accuses, long? dernierCentral, CancellationToken ct)
    {
        var avance = p.RetourArriereSequencesLe is null ? await AvancerSequencesAsync(p.Adresse!, p.Epoque, ct) : PhraseAvanceFaite;
        var message = MessageRetourArriere(p.RetourArriereCentral ?? dernierCentral ?? 0, p.Epoque, p.DernierLot) + avance;
        await depot.NoterEnvoiAsync(message, ct);
        // Pas une erreur d'envoi « persistante » : l'alerte propre à la détection (une par détection) remplace l'alerte horaire
        await depot.NoterPassageAsync(null, ct);
        return new(accuses, null, message, Suspendu: true);
    }

    /// <summary>Décision du titulaire (2026-11-18) : dite dans l'alerte et à l'écran Synchronisation.</summary>
    public const string PhraseIntegrite = "Des anomalies d’intégrité sont attendues au serveur central pour les rangs restaurés (journal des ventes et "
        + "autres journaux chaînés) : elles ne signifient pas une falsification.";

    /// <summary>Gardée dans le message tant que l'avance est faite (pas seulement au passage qui l'a faite).</summary>
    public const string PhraseAvanceFaite = " N° des nouvelles opérations avancés au-delà de ceux du serveur central.";

    public static string MessageRetourArriere(long central, int epoque, long local) =>
        $"Base revenue en arrière (restauration ou bascule) : le serveur central a reçu jusqu’au lot {central} de l’époque {epoque}, "
        + $"cette base jusqu’au lot {local}. Le titulaire doit repartir sur une nouvelle époque dans Synchronisation. " + PhraseIntegrite;

    /// <summary>Plafonds de la réponse des maximums (noms de table PostgreSQL, n° positifs).</summary>
    public const int MaxTablesMaximums = 1000;

    /// <summary>Avance des séquences ; renvoie le complément du message (fait, ou pourquoi pas).</summary>
    private async Task<string> AvancerSequencesAsync(string adresse, int epoque, CancellationToken ct)
    {
        IReadOnlyDictionary<string, long> maximums;
        try { maximums = await client.MaximumsAsync(adresse, ct); }
        catch (ServeurCentralException e) when (e.CodeHttp == 404)
        {
            return " Avance des n° non faite : le serveur central ne connaît pas encore la route des maximums (script central 020) ; nouvel essai au prochain passage.";
        }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return $" Avance des n° non faite ({e.Message}) ; nouvel essai au prochain passage.";
        }
        var retenus = maximums.Where(m => m.Value >= 1 && NomTableValide(m.Key)).Take(MaxTablesMaximums)
            .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal);
        // Compteur des tickets AVANT les séquences : l'avance des séquences pose « avance faite » ; un échec du compteur doit donc
        // l'empêcher, pour que tout soit réessayé au passage suivant (l'avance du compteur ne recule jamais : sans effet si déjà faite)
        var tickets = await AvancerCompteurTicketAsync(maximums, epoque, ct);
        if (tickets.StartsWith(PhraseTicketNonAvance, StringComparison.Ordinal)) return tickets;
        IReadOnlyList<SequenceAvancee> faites;
        try { faites = await depot.AvancerSequencesAsync(retenus, ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Erreur de la base (séquence trop petite pour le n° du central…) : la suspension et son message clair restent
            return $" Avance des n° non faite (erreur de la base : {e.GetType().Name}) ; nouvel essai au prochain passage." + tickets;
        }
        var echecs = faites.Where(s => s.Erreur is not null).Select(s => s.Table).ToList();
        var avancees = faites.Count(s => s.Erreur is null && s.Apres > s.Avant);
        if (echecs.Count > 0)
            return $" Avance des n° incomplète : {echecs.Count} table(s) en échec ({string.Join(", ", echecs.Take(10))}{(echecs.Count > 10 ? ", …" : "")}), "
                + $"{avancees} autre(s) avancée(s) ; nouvel essai au prochain passage." + tickets;
        return $" N° des nouvelles opérations avancés au-delà de ceux du serveur central ({avancees} table(s) avancée(s) sur {faites.Count})." + tickets;
    }

    /// <summary>Entrée réservée de la réponse des maximums (script central 024) : plus grand n° de ticket reçu par le central.</summary>
    public const string CleTicketCentral = "#ticket";

    /// <summary>Lot S6.5.4 (S6.5-D6 a) : compteur des tickets avancé au-delà du plus grand n° reçu par le central ; complément du message.</summary>
    private async Task<string> AvancerCompteurTicketAsync(IReadOnlyDictionary<string, long> maximums, int epoque, CancellationToken ct)
    {
        if (!maximums.TryGetValue(CleTicketCentral, out var dernier) || dernier < 1) return "";
        try
        {
            var sautes = await depot.AvancerCompteurTicketAsync(dernier, epoque, ct);
            return sautes > 0 ? $" Compteur des tickets avancé au-delà du n° {dernier} reçu par le serveur central ({sautes} n° sauté(s), plage notée)." : "";
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return $"{PhraseTicketNonAvance} (erreur de la base : {e.GetType().Name}) ; avance des n° et du compteur réessayée au prochain passage.";
        }
    }

    private const string PhraseTicketNonAvance = " Compteur des tickets non avancé";

    private static bool NomTableValide(string nom) =>
        nom.Length is >= 1 and <= 63 && (char.IsAsciiLetterLower(nom[0]) || nom[0] == '_') && nom.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    private async Task RenouvelerAsync(string adresse, CancellationToken ct)
    {
        var demande = certificat.NouvelleDemande();
        var r = await client.RenouvelerAsync(adresse, demande, ct);
        certificat.Installer(r.CertificatPem, r.AutoritePem, null);
    }

    /// <summary>
    /// Renouvellement tenté à chaque passage dans les 60 jours de l'échéance ; un échec (refus, quota d'un renouvellement par jour,
    /// central injoignable) ne fait jamais échouer le passage : rendu comme une note, nouvel essai au passage suivant.
    /// </summary>
    private async Task<string?> EssayerRenouvelerAsync(string adresse, DateTime echeanceUtc, CancellationToken ct)
    {
        try
        {
            await RenouvelerAsync(adresse, ct);
            return null;
        }
        catch (Exception e) when (e is ServeurCentralException or HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            var cause = e is ServeurCentralException s ? s.Message : "serveur central injoignable";
            return $"Certificat de synchronisation non renouvelé ({cause}) : nouvel essai au prochain passage, échéance le {echeanceUtc:dd/MM/yyyy}.";
        }
    }

    private async Task<ResultatEnvoi> ErreurAsync(int accuses, string erreur, bool bloquant, CancellationToken ct, bool quota = false)
    {
        await depot.NoterEnvoiAsync(erreur, ct);
        await depot.NoterPassageAsync(quota ? NaturesErreurSync.Quota : NaturesErreurSync.Autre, ct);
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
/// <param name="DelaiControleDemarrage">Script 152 : attente maximale de l'encaissement au démarrage, en secondes (0 à 600) ; null : inchangée.</param>
public sealed record ReglagesSync(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins, int? DelaiControleDemarrage = null);

/// <param name="chargements">Lot S6 : réactivation d'une officine inscrite = chargement complet (les modifications faites
/// pendant la désactivation n'ont pas été inscrites) ; null : aucun.</param>
public sealed class ReglerSync(IEnvoiSyncDepot depot, IJournal journal, IChargementSyncDepot? chargements = null,
    Reglages.LibererReglagesMutualises? liberation = null)
{
    /// <summary>Script 152 : plafond de l'attente de l'encaissement au démarrage (10 minutes).</summary>
    public const int DelaiControleDemarrageMax = 600;

    public static readonly IReadOnlySet<string> ModesTextes = new HashSet<string>(StringComparer.Ordinal) { "filtres", "retires" };

    public static bool AdresseValide(string? a) =>
        string.IsNullOrWhiteSpace(a) || (Uri.TryCreate(a.Trim(), UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(u.Query) && u.AbsolutePath == "/" && string.IsNullOrEmpty(u.UserInfo));

    public async Task ExecuterAsync(ReglagesSync r, long utilisateurId, CancellationToken ct = default)
    {
        if (!AdresseValide(r.Adresse)) throw new RegleMetierException("Adresse du serveur central : https://nom-du-serveur[:port] attendu.", "adresse");
        if (!ModesTextes.Contains(r.TextesLibres)) throw new RegleMetierException("Textes libres : « filtres » ou « retires ».", "textes_libres");
        if (r.DelaiControleDemarrage is < 0 or > DelaiControleDemarrageMax)
            throw new RegleMetierException($"Attente au démarrage : entre 0 et {DelaiControleDemarrageMax} secondes.", "delai_controle_demarrage");
        var avant = await depot.ParametresAsync(ct);
        var adresse = string.IsNullOrWhiteSpace(r.Adresse) ? null : r.Adresse.Trim().TrimEnd('/');
        // Désactivation (D3) : les réglages imposés par le serveur central redeviennent modifiables aussitôt (mode en vigueur « local »), valeurs gardées ;
        // une ligne par table au journal, écrite AVANT la désactivation pour que l'état de chaque table soit encore celui reçu du serveur central
        if (avant.Active && !r.Active && liberation is not null) await liberation.ExecuterAsync(utilisateurId, ct);
        await depot.ReglerAsync(adresse, r.TextesLibres, r.EnvoyerMedecins, r.Active, ct);
        if (r.DelaiControleDemarrage is { } delai && delai != avant.DelaiControleDemarrage) await depot.ReglerControleDemarrageAsync(delai, ct);
        if (!avant.Active && r.Active && avant.InscriteLe is not null && chargements is not null)
            await chargements.DemanderAsync(MotifsChargement.Reactivation, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "reglages_synchronisation", "sync_parametre", 1, new Dictionary<string, object?>
        {
            ["active"] = $"{avant.Active} -> {r.Active}",
            ["adresse"] = $"{avant.Adresse ?? "-"} -> {adresse ?? "-"}",
            ["textes_libres"] = $"{avant.TextesLibres} -> {r.TextesLibres}",
            ["envoyer_medecins"] = $"{avant.EnvoyerMedecins} -> {r.EnvoyerMedecins}",
            ["delai_controle_demarrage"] = $"{avant.DelaiControleDemarrage} -> {r.DelaiControleDemarrage ?? avant.DelaiControleDemarrage}"
        }, null, ct);
    }
}
