using System.Security.Cryptography;
using System.Text;

namespace Pharma.Shared;

// ---- Échanges officine <-> serveur central (lot S2, docs/conception-synchronisation.md § 12) ----
// Partagés par l'API de l'officine (client) et l'API centrale (serveur).

/// <summary>Activation : code à usage unique + demande de certificat (CSR PEM, clé générée dans l'officine) + code de l'officine installée.</summary>
public sealed record DemandeActivationDto(string Code, string DemandePem, Guid OfficineId);

/// <summary>Certificat client délivré (PEM), certificat de l'autorité à épingler (PEM), échéance.</summary>
public sealed record ReponseActivationDto(Guid OfficineId, string Serveur, string CertificatPem, string AutoritePem, DateTimeOffset Echeance);

/// <summary>Renouvellement (avant échéance) : nouvelle demande signée par une nouvelle clé.</summary>
public sealed record DemandeRenouvellementDto(string DemandePem);

public sealed record ReponseJetonDto(string Jeton, DateTimeOffset Expire);

/// <summary>Accusé d'un lot : statut (accepte, deja_recu, trou, empreinte_differente), dernier lot reçu pour l'époque et changements
/// descendants non encore appliqués par l'officine (si elle a donné sa version dans l'en-tête X-Sync-Descendants).</summary>
public sealed record AccuseLotDto(int Epoque, long Numero, string Statut, long DernierRecu, ChangementsDescendantsDto? Descendants = null);

/// <summary>
/// Voie descendante (contrat desc-v1, première brique de S3) : éléments que le serveur central fait descendre dans la base de l'officine.
/// Chaque élément : table locale de destination, clé, version (monotone par officine) et données (objet JSON). L'officine n'applique
/// jamais une version plus ancienne que celle qu'elle a, et redonne la plus grande version appliquée à l'échange suivant (acquittement) ;
/// le serveur central ne renvoie que les versions plus grandes.
/// </summary>
public sealed record ChangementsDescendantsDto(string Contrat, long VersionMax, IReadOnlyList<ChangementDescendantDto> Changements)
{
    public const string ContratV1 = "desc-v1";
    /// <summary>En-tête HTTP portant la plus grande version descendante appliquée par l'officine.</summary>
    public const string EnTeteVersion = "X-Sync-Descendants";
}

public sealed record ChangementDescendantDto(string Table, string Cle, long Version, string DonneesJson);

/// <summary>Échange léger (chaque passage, même sans lot à envoyer) : version descendante appliquée par l'officine.</summary>
public sealed record EchangeDescendantDto(long VersionAppliquee);

/// <summary>Réponse de GET /api/v1/etat. EpoqueMax (facultatif, 2026-11-18) : plus grande époque reçue de l'officine appelante ; absent
/// (null) pour un serveur central antérieur. Une officine antérieure ignore ce champ.</summary>
public sealed record EtatCentralDto(int Epoque, long DernierRecu, int? EpoqueMax = null);

/// <summary>
/// Lot S6 (conception-synchronisation.md § 30) : réponse de GET /api/v1/maximums, réservée à l'officine appelante. Pour chaque table
/// miroir dont la clé est la seule colonne « id » (bigint), le plus grand id reçu de cette officine, toutes époques confondues
/// (lignes absentes et archivées comprises). Aucune donnée de ligne. Sert à avancer les séquences locales après une restauration.
/// </summary>
public sealed record MaximumsCentralDto(IReadOnlyDictionary<string, long> Tables)
{
    /// <summary>Lot S6.5.4 (S6.5-D6 a) : entrée réservée (jamais un nom de table) portant le plus grand n° de ticket reçu de l'officine.</summary>
    public const string CleTicket = "#ticket";
}

/// <summary>
/// Lot S6.5 (conception-synchronisation.md § 31) : fin d'une chaîne de l'officine au moment du changement d'époque : journal, clé de
/// chaîne, dernier rang, n° de la ligne et empreinte (hexadécimal, 64 ou 128 caractères). Rang 0 : journal vide.
/// </summary>
public sealed record FinChaineDto(string Journal, string CleChaine, long Rang, string? Id, string? Hash);

/// <summary>
/// Lot S6.5 (S6.5-D1) : déclaration POST /api/v1/epoque, envoyée par l'officine après « Repartir sur une nouvelle époque », AVANT tout
/// lot de la nouvelle époque (le central ne classe une restauration que si la déclaration est arrivée avant). Aucune donnée de
/// ligne, aucun nom : n° du titulaire seulement. Réponse : <see cref="ReponseDeclarationEpoqueDto"/> ; renvoi identique accepté.
/// </summary>
public sealed record DeclarationEpoqueDto(int AncienneEpoque, int NouvelleEpoque, long DernierLotLocal, long? DernierLotCentral,
    DateTimeOffset? DetecteLe, DateTimeOffset DecideLe, long? TitulaireId, IReadOnlyList<FinChaineDto> FinsChaine)
{
    /// <summary>Motif du refus, ou null si la déclaration est recevable (mêmes bornes que la fonction du central).</summary>
    public string? Refus() =>
        AncienneEpoque < 1 || NouvelleEpoque <= AncienneEpoque ? "Époques hors bornes : la nouvelle doit suivre l'ancienne."
        : DernierLotLocal < 0 || DernierLotCentral < 0 ? "N° de lot hors bornes."
        : FinsChaine is null || FinsChaine.Count > 20 ? "Fins de chaîne attendues (20 au plus)."
        : FinsChaine.Any(f => f is null || f.Rang < 0 || string.IsNullOrEmpty(f.Journal) || f.Journal.Length > 63
              || !f.Journal.All(c => c is (>= 'a' and <= 'z') or '_') || string.IsNullOrEmpty(f.CleChaine) || f.CleChaine.Length > 100
              || (f.Id is { Length: > 40 }) || (f.Hash is not null && (f.Hash.Length is not (64 or 128) || !f.Hash.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))))
            ? "Fin de chaîne invalide."
        : null;
}

/// <summary>Réponse de POST /api/v1/epoque : n° de la déclaration, date de réception au central, renvoi d'une déclaration déjà reçue,
/// et si elle est arrivée avant tout lot de la nouvelle époque (condition 1 du § 31.2).</summary>
public sealed record ReponseDeclarationEpoqueDto(long Id, DateTimeOffset RecuLe, bool DejaDeclaree, bool AvantPremierLot);

/// <summary>
/// Début d'un chargement complet (lot S6) : n° local du chargement, époque, repère (n° de file des lignes de l'instantané),
/// dernier lot accusé dans l'officine (si le serveur central ne l'a plus, les lots manquants sont abandonnés et remplacés par
/// le chargement), motif et tables chargées.
/// </summary>
public sealed record DebutChargementDto(int Numero, int Epoque, long Repere, long ReprendreApres, string Motif, IReadOnlyList<string> Tables)
{
    public static readonly IReadOnlySet<string> Motifs = new HashSet<string>(StringComparer.Ordinal) { "activation", "demande", "reactivation", "lots_manquants" };

    /// <summary>Motif du refus, ou null si la demande est recevable.</summary>
    public string? Refus() =>
        Numero < 1 || Epoque < 1 || Repere < 0 || ReprendreApres < 0 ? "N° de chargement, époque, repère ou reprise hors bornes."
        : Motif is null || !Motifs.Contains(Motif) ? "Motif de chargement inconnu."
        : Tables is null || Tables.Count is 0 or > 300 || Tables.Any(t => t is null || t.Length > 63 || !t.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_'))
            ? "Liste des tables invalide."
        : null;
}

/// <summary>Début accusé : dernier lot attendu avant les suivants, nombre de lots abandonnés (perdus par le serveur central).</summary>
public sealed record ReponseDebutChargementDto(int Numero, long DernierRecu, long LotsAbandonnes);

/// <summary>Fin d'un chargement : nombre de lignes de l'instantané par table.</summary>
public sealed record FinChargementDto(int Numero, IReadOnlyDictionary<string, long> Lignes);

/// <summary>Fin accusée : lignes du serveur central absentes de l'officine (signalées, jamais effacées), par table.</summary>
public sealed record ReponseFinChargementDto(int Numero, long Absentes, IReadOnlyDictionary<string, long> AbsentesParTable, IReadOnlyDictionary<string, long> LignesVues);

/// <summary>
/// Familles de données nominatives réglables au serveur central (2026-11-16, docs/conception-synchronisation.md § 14). Une colonne
/// d'une famille non autorisée ne quitte jamais l'officine (retirée à la constitution des lots, script 130) et le serveur central
/// l'efface s'il la reçoit quand même (script central 004). Même liste dans database/migration/130 (sync_famille_colonne) et
/// database/cloud/004 (cloud_famille_colonne) : test FamillesNominativesTests.
/// </summary>
public static class FamillesNominativesSync
{
    public const string Employes = "employes";
    public const string Pharmacie = "pharmacie";
    public const string Fournisseurs = "fournisseurs";
    public const string CourrielCommandes = "courriel_commandes";
    public const string Banque = "banque";
    public const string ClientsFactures = "clients_factures";

    /// <summary>Famille -> (libellé, défaut au serveur central, colonnes « table.colonne »).</summary>
    public static readonly IReadOnlyDictionary<string, (string Libelle, bool Defaut, IReadOnlyList<string> Colonnes)> Familles =
        new Dictionary<string, (string, bool, IReadOnlyList<string>)>(StringComparer.Ordinal)
        {
            [Employes] = ("Noms et identifiants du personnel", false, ["utilisateur.nom_complet", "utilisateur.identifiant"]),
            [Pharmacie] = ("Nom, titulaire, identifiants fiscaux et coordonnées de la pharmacie", true,
                ["pharmacie.titulaire", "pharmacie.adresse", "pharmacie.telephone", "parametre_notification.smtp_expediteur",
                 "pharmacie.nom", "pharmacie.nif", "pharmacie.nis", "pharmacie.rc", "pharmacie.article_imposition"]),
            [Fournisseurs] = ("Coordonnées des fournisseurs", true, ["fournisseur.telephone", "fournisseur.email", "fournisseur.adresse"]),
            [CourrielCommandes] = ("Adresse courriel des commandes envoyées", true, ["commande_fournisseur.envoyee_email_a"]),
            [Banque] = ("Coordonnées bancaires (n° de compte, banque, références, prêteur)", false,
                ["compte_tresorerie.numero_compte", "pret_bancaire.preteur",
                 "compte_tresorerie.banque", "reglement_fournisseur.banque", "reglement_fournisseur.reference",
                 "paiement_effet.reference", "remise_cheques.reference", "pret_bancaire.reference"]),
            [ClientsFactures] = ("Adresse des clients facturés", false, ["client_facture.adresse"])
        };
}

public static class StatutsLot
{
    public const string Accepte = "accepte";
    public const string DejaRecu = "deja_recu";
    public const string Trou = "trou";
    public const string EmpreinteDifferente = "empreinte_differente";
}

/// <summary>
/// Code d'activation : secret à usage unique (20 caractères, 4 groupes de 5, sans I, O, 0, 1), puis « . », puis les 32 premiers
/// caractères hexadécimaux de l'empreinte SHA-256 de l'autorité du serveur central (épinglage dès la première connexion).
/// Seule l'empreinte SHA-256 du secret normalisé est gardée par le serveur central.
/// </summary>
public static class CodeActivationSync
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    public const int LongueurEpinglage = 32;

    public static string Generer(string empreinteAutorite)
    {
        var o = RandomNumberGenerator.GetBytes(20);
        var s = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            if (i > 0 && i % 5 == 0) s.Append('-');
            s.Append(Alphabet[o[i] % Alphabet.Length]);
        }
        return s + "." + empreinteAutorite[..LongueurEpinglage].ToLowerInvariant();
    }

    /// <summary>Secret normalisé et épinglage ; null si le code est mal formé.</summary>
    public static (string Secret, string Epinglage)? Lire(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var parties = code.Trim().Split('.');
        if (parties.Length != 2) return null;
        var secret = new string(parties[0].Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
        var pin = parties[1].Trim().ToLowerInvariant();
        if (secret.Length != 20 || secret.Any(c => !Alphabet.Contains(c))) return null;
        if (pin.Length != LongueurEpinglage || pin.Any(c => !Uri.IsHexDigit(c))) return null;
        return (secret, pin);
    }

    public static string EmpreinteSecret(string secretNormalise) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secretNormalise)));
}

// ---- Écran poste « Admin › Synchronisation » (lot S2) ----

/// <summary>État de la synchronisation de cette officine (vu du serveur qui écrit).</summary>
public sealed record EtatSynchronisationDto(bool Active, string? OfficineId, string? Adresse, bool Activee, DateTime? InscriteLe,
    DateTime? DernierEnvoi, DateTime? DernierAccuseLe, long DernierLot, long DernierAccuse, long LignesEnAttente, long LotsEnAttente,
    DateTime? EcheanceCertificat, string? EmpreinteCertificat, string? DerniereErreur, DateTime? DerniereErreurLe, string TextesLibres,
    bool EnvoyerMedecins, int Epoque, string VersionContrat, long LotsAutresEpoques = 0, bool BaseEnLectureSeule = false,
    ChargementSynchronisationDto? Chargement = null, IReadOnlyList<string>? FamillesEnvoyees = null, DateTime? FamillesLuesLe = null,
    long FamillesVersion = 0, SupervisionSynchronisationDto? Supervision = null, IntegriteCentraleDto? IntegriteCentrale = null,
    RetourArriereSynchronisationDto? RetourArriere = null, int DelaiControleDemarrage = 120);

/// <summary>
/// Lot S6.2 : base de l'officine revenue en arrière (restauration, bascule) détectée ; envoi suspendu jusqu'à « Repartir sur une
/// nouvelle époque » (titulaire). Dernier lot de l'époque vu au serveur central et formé ici, avance des n° faite, alerte envoyée.
/// </summary>
public sealed record RetourArriereSynchronisationDto(DateTime DetecteLe, int Epoque, long DernierLotCentral, long DernierLotLocal,
    bool SequencesAvancees, DateTime? AlerteLe);

/// <summary>« Repartir sur une nouvelle époque » : époque affichée à l'écran (confirmation ; refus si elle a changé entre-temps).</summary>
/// <param name="MotDePasse">Mot de passe du titulaire ressaisi (validation de l'action sensible, 2026-11-18).</param>
public sealed record NouvelleEpoqueSynchronisationDto(int EpoqueAffichee, string? MotDePasse = null);

public sealed record ResultatNouvelleEpoqueSynchronisationDto(int Epoque, int AncienneEpoque, int? Chargement);

/// <summary>
/// Lot S4 : dernière vérification d'intégrité faite par le serveur central (résumé reçu par la voie descendante, format integrite-v1).
/// Statut : ok, attente (une ligne, un ticket ou un sceau attendu n'est pas encore arrivé) ou anomalie.
/// </summary>
public sealed record IntegriteCentraleDto(string Statut, DateTime? VerifieLe, DateTime? CompleteLe, long AnomaliesTotal, long DerniereAnomalieId,
    long SceauxOk, long SceauxAttente, long SceauxIncoherents, long? TicketsVerifiesJusquA, IReadOnlyList<ChaineIntegriteCentraleDto> Chaines,
    IReadOnlyList<AnomalieIntegriteCentraleDto> Anomalies, IReadOnlyList<RestaurationCentraleDto>? Restaurations = null, long AnomaliesClassees = 0);

/// <summary>Lot S6.5 : restauration constatée au central (suite remplacée gardée comme preuve) ; PriseActe : cause notée par le titulaire.</summary>
public sealed record RestaurationCentraleDto(long Id, string Journal, int AncienneEpoque, int NouvelleEpoque, long RangCommun, long? FinAncienneRang,
    long Lignes, DateTime? Du, DateTime? Au, decimal? MontantTtc, long SceauxPerdus, string Classe, bool PriseActe,
    IReadOnlyList<string>? TicketsReattribues = null);

/// <summary>Lot S6.5.4 : prise d'acte du titulaire d'une restauration constatée (époque de la restauration, cause 3 à 500 caractères).</summary>
public sealed record PriseActeRestaurationDto(int NouvelleEpoque, string Cause);

/// <summary>Chaîne vérifiée au central : mode « complet » (empreintes recalculées) ou « liaison » (rangs et chaînage seulement).</summary>
public sealed record ChaineIntegriteCentraleDto(string Journal, string Cle, string Mode, string Statut, long DernierRang, long NonVerifiables);

/// <summary>Anomalie constatée au central (20 plus récentes) ; Nature : code, Libelle : texte affiché.</summary>
public sealed record AnomalieIntegriteCentraleDto(long Id, string Nature, string Libelle, string Journal, long? Rang, string? Ligne, string? Detail,
    DateTime? DetecteeLe, bool Classee = false);

/// <summary>
/// Dernier chargement complet (lot S6) : état (demande, constitution, pret, annonce, termine, remplace), progression (lignes
/// inscrites, dans un lot accusé ; tables faites pendant la constitution), lignes du serveur central absentes de l'officine.
/// </summary>
public sealed record ChargementSynchronisationDto(int Numero, string Motif, string Etat, DateTime DemandeLe, long LignesTotal,
    long LignesAccusees, int TablesFaites, int TablesTotal, DateTime? TermineLe, long? AbsentesCentral);

/// <summary>Supervision (lot S3) : réglages du bandeau, des alertes et de la purge, ce qui attend, état de la purge de la file.</summary>
public sealed record SupervisionSynchronisationDto(int BandeauMinutes, int RetardHeures, int ErreurMinutes, bool PurgeActive, int PurgeJours,
    DateTime? AttenteDepuis, long AttenteOperations, string? ErreurNature, DateTime? ErreurDepuisLe, PurgeSynchronisationDto Purge);

/// <summary>Purge de sync_sortie : lots purgés jusqu'au n° (époque), dernière exécution, cumul, taille de la file.</summary>
public sealed record PurgeSynchronisationDto(int? EpoquePurge, long DernierLotPurge, DateTime? DernierePurgeLe, long DernieresLignes, long TotalLignes,
    string? DerniereErreur, long? LignesGardees, long OctetsFile);

public sealed record ReglagesSupervisionSynchronisationDto(int BandeauMinutes, int RetardHeures, int ErreurMinutes, bool PurgeActive, int PurgeJours);

/// <summary>Bandeau « Synchronisation en attente » d'un poste : Affiche faux = rien à montrer (désactivée, sous le seuil, droit absent).</summary>
public sealed record AttenteSynchronisationDto(bool Affiche, DateTime? Depuis, long Operations);

/// <param name="DelaiControleDemarrage">Script 152 : attente maximale de l'encaissement au démarrage, en secondes (0 à 600) ; null : inchangée.</param>
public sealed record ReglagesSynchronisationDto(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins, int? DelaiControleDemarrage = null);

/// <summary>Script 152 : secondes avant que l'encaissement soit permis (0 : permis) ; bandeau de la Caisse au démarrage.</summary>
public sealed record AttenteEncaissementDto(int Secondes);

public sealed record ActivationSynchronisationDto(string Code);

/// <param name="Avertissement">Envoi suspendu (base revenue en arrière) : message à montrer comme un avertissement, pas une erreur.</param>
public sealed record ResultatEnvoiSynchronisationDto(int LotsAccuses, string? Arret, string? Erreur, string? Avertissement = null);

/// <summary>Lots S5.1 et S5.5 : ordre reçu du serveur central (Resume : texte lisible, employé et rôle nommés d'après la base de l'officine).</summary>
public sealed record OrdreCentralDto(Guid Id, string Type, string Libelle, string Resume, string Auteur, DateTime CreeLe, DateTime ExpireLe,
    string Statut, string? Motif, DateTime RecuLe, DateTime? TraiteLe, bool DemandeMotDePasse);

/// <summary>Page d'ordres reçus ; AConfirmer : nombre d'ordres qui attendent la confirmation du titulaire.</summary>
public sealed record PageOrdresCentrauxDto(PageDto<OrdreCentralDto> Page, int AConfirmer);

public sealed record ConfirmerOrdreCentralDto(string? MotDePasse);

public sealed record RefuserOrdreCentralDto(string? Motif);
