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

/// <summary>Accusé d'un lot : statut (accepte, deja_recu, trou, empreinte_differente) et dernier lot reçu pour l'époque.</summary>
public sealed record AccuseLotDto(int Epoque, long Numero, string Statut, long DernierRecu);

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
    ChargementSynchronisationDto? Chargement = null);

/// <summary>
/// Dernier chargement complet (lot S6) : état (demande, constitution, pret, annonce, termine, remplace), progression (lignes
/// inscrites, dans un lot accusé ; tables faites pendant la constitution), lignes du serveur central absentes de l'officine.
/// </summary>
public sealed record ChargementSynchronisationDto(int Numero, string Motif, string Etat, DateTime DemandeLe, long LignesTotal,
    long LignesAccusees, int TablesFaites, int TablesTotal, DateTime? TermineLe, long? AbsentesCentral);

public sealed record ReglagesSynchronisationDto(bool Active, string? Adresse, string TextesLibres, bool EnvoyerMedecins);

public sealed record ActivationSynchronisationDto(string Code);

public sealed record ResultatEnvoiSynchronisationDto(int LotsAccuses, string? Arret, string? Erreur);
