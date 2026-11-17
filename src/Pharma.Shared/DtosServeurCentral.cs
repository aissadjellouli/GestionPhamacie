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

public sealed record EtatCentralDto(int Epoque, long DernierRecu);

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
    long FamillesVersion = 0, SupervisionSynchronisationDto? Supervision = null, IntegriteCentraleDto? IntegriteCentrale = null);

/// <summary>
/// Lot S4 : dernière vérification d'intégrité faite par le serveur central (résumé reçu par la voie descendante, format integrite-v1).
/// Statut : ok, attente (une ligne, un ticket ou un sceau attendu n'est pas encore arrivé) ou anomalie.
/// </summary>
public sealed record IntegriteCentraleDto(string Statut, DateTime? VerifieLe, DateTime? CompleteLe, long AnomaliesTotal, long DerniereAnomalieId,
    long SceauxOk, long SceauxAttente, long SceauxIncoherents, long? TicketsVerifiesJusquA, IReadOnlyList<ChaineIntegriteCentraleDto> Chaines,
    IReadOnlyList<AnomalieIntegriteCentraleDto> Anomalies);

/// <summary>Chaîne vérifiée au central : mode « complet » (empreintes recalculées) ou « liaison » (rangs et chaînage seulement).</summary>
public sealed record ChaineIntegriteCentraleDto(string Journal, string Cle, string Mode, string Statut, long DernierRang, long NonVerifiables);

/// <summary>Anomalie constatée au central (20 plus récentes) ; Nature : code, Libelle : texte affiché.</summary>
public sealed record AnomalieIntegriteCentraleDto(long Id, string Nature, string Libelle, string Journal, long? Rang, string? Ligne, string? Detail,
    DateTime? DetecteeLe);

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

public sealed record ReglagesSynchronisationDto(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins);

public sealed record ActivationSynchronisationDto(string Code);

public sealed record ResultatEnvoiSynchronisationDto(int LotsAccuses, string? Arret, string? Erreur);
