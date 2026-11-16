using Pharma.Domain.Commun;

namespace Pharma.Application.Serialisation;

/// <summary>Réponse de la base nationale de sérialisation pour une boîte.</summary>
public enum StatutSerialisation { Authentique, Inconnue, DejaDecommissionnee, Rappelee, Expiree }

/// <summary>Opération demandée à la base nationale (colonne verification_serialisation.operation).</summary>
public enum OperationSerialisation { Verifier, Decommissionner, Reactiver }

/// <summary>Identité d'une boîte au sens de la sérialisation : GTIN, numéro de série, lot, péremption.</summary>
public sealed record BoiteSerialisee(string Gtin, string NumeroSerie, string? Lot, DateOnly? Peremption);

/// <summary>Réponse du connecteur : seule « authentique » accepte l'opération.</summary>
public sealed record ReponseSerialisation(StatutSerialisation Statut, string? Message = null)
{
    public bool Acceptee => Statut == StatutSerialisation.Authentique;
}

/// <summary>Le connecteur ne répond pas (hors ligne, délai dépassé) : l'opération reste en file et sera rejouée.</summary>
public sealed class ConnecteurIndisponibleException(string message, Exception? interne = null) : Exception(message, interne);

/// <summary>
/// Port vers la base nationale de sérialisation (M1). Adaptateur actuel : factice (SerialisationFactice, réponses paramétrables) ;
/// l'adaptateur réel (base nationale du MIPH) viendra avec la version cloud [À VÉRIFIER : API MIPH].
/// Lève <see cref="ConnecteurIndisponibleException"/> quand la base ne répond pas.
/// </summary>
public interface ISerialisationConnecteur
{
    string Nom { get; }
    Task<ReponseSerialisation> VerifierAsync(BoiteSerialisee boite, CancellationToken ct = default);
    /// <summary>Boîte vendue : retirée du circuit national.</summary>
    Task<ReponseSerialisation> DecommissionnerAsync(BoiteSerialisee boite, CancellationToken ct = default);
    /// <summary>Retour patient ou vente annulée : boîte remise dans le circuit.</summary>
    Task<ReponseSerialisation> ReactiverAsync(BoiteSerialisee boite, CancellationToken ct = default);
}

/// <summary>Opération en attente dans la file (verification_serialisation), avec la boîte et le produit.</summary>
public sealed record OperationEnFile(long Id, long BoiteId, OperationSerialisation Operation, BoiteSerialisee Boite, string Produit, int Essais);

/// <summary>Ligne de la file pour l'écran Administration › Sérialisation.</summary>
public sealed record LigneFileSerialisation(long Id, long BoiteId, string Produit, string Gtin, string NumeroSerie, string? Lot,
    OperationSerialisation Operation, string Statut, StatutSerialisation? Resultat, string? Message, int Essais,
    DateTime CreeLe, DateTime? DernierEssaiLe, DateTime? TraiteLe);

public sealed record EtatFileSerialisation(int EnAttente, int Reussies, int Refusees, DateTime? DernierEssaiLe, string? DerniereErreur);

/// <summary>Réglages (parametre_serialisation) : file active, connecteur, panne simulée de l'adaptateur factice.</summary>
public sealed record ParametresSerialisation(bool Actif, string Connecteur, bool FacticeHorsLigne);

/// <summary>File d'attente des opérations de sérialisation (table verification_serialisation, script 120).</summary>
public interface IFileSerialisation
{
    /// <summary>Opérations en attente dont l'heure de rejeu est passée, des plus anciennes aux plus récentes.</summary>
    Task<IReadOnlyList<OperationEnFile>> AFaireAsync(int maximum, CancellationToken ct = default);
    /// <summary>Réponse reçue : réussie (acceptée) ou refusée, résultat et message tracés.</summary>
    Task TerminerAsync(long id, bool acceptee, StatutSerialisation resultat, string? message, bool alerteTitulaire, CancellationToken ct = default);
    /// <summary>Connecteur injoignable : essai compté, rejeu reporté (délai croissant, au plus 30 minutes).</summary>
    Task ReporterAsync(long id, string message, CancellationToken ct = default);
    Task<EtatFileSerialisation> EtatAsync(CancellationToken ct = default);
    /// <summary>Lignes de la file (statut : en_attente, reussie, refusee ou null pour toutes), les plus récentes d'abord.</summary>
    Task<(IReadOnlyList<LigneFileSerialisation> Lignes, int Total)> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default);
    /// <summary>Relance : une opération (en attente ou refusée) ou toutes celles en attente, rejouées tout de suite. Nombre relancé.</summary>
    Task<int> RelancerAsync(long? id, long utilisateurId, CancellationToken ct = default);
    Task<ParametresSerialisation> ParametresAsync(CancellationToken ct = default);
    Task EnregistrerParametresAsync(bool actif, bool facticeHorsLigne, long utilisateurId, CancellationToken ct = default);
    /// <summary>Identifiants d'application GS1 paramétrés (parametre_gs1_ai, actifs).</summary>
    Task<IReadOnlyList<Gs1Identifiant>> IdentifiantsGs1Async(CancellationToken ct = default);
}

/// <summary>Règle de réponse de l'adaptateur factice (table serialisation_reponse_factice) : critère null = toute valeur.</summary>
public sealed record RegleReponseFactice(string? Gtin, string? NumeroSerie, string? Lot, OperationSerialisation? Operation, StatutSerialisation Resultat);

/// <summary>Réponses déterministes de l'adaptateur factice (testables sans base).</summary>
public static class ReponsesFactices
{
    /// <summary>
    /// Première règle qui correspond (dans l'ordre donné) ; à défaut « expirée » si la péremption est passée (vérification et vente ;
    /// pas la réactivation d'un retour), sinon « authentique ».
    /// </summary>
    public static ReponseSerialisation Repondre(IReadOnlyList<RegleReponseFactice> regles, OperationSerialisation operation,
        BoiteSerialisee boite, DateOnly aujourdhui)
    {
        var regle = regles.FirstOrDefault(r =>
            (r.Gtin is null || r.Gtin == boite.Gtin) && (r.NumeroSerie is null || r.NumeroSerie == boite.NumeroSerie) &&
            (r.Lot is null || r.Lot == boite.Lot) && (r.Operation is null || r.Operation == operation));
        if (regle is not null)
            return new ReponseSerialisation(regle.Resultat, $"Réponse factice paramétrée : {LibellesSerialisation.Texte(regle.Resultat)}");
        if (operation != OperationSerialisation.Reactiver && boite.Peremption is { } p && p < aujourdhui)
            return new ReponseSerialisation(StatutSerialisation.Expiree, "Réponse factice : péremption dépassée");
        return new ReponseSerialisation(StatutSerialisation.Authentique);
    }
}

/// <summary>Bilan d'un passage de la tâche de fond.</summary>
public sealed record BilanFileSerialisation(int Reussies, int Refusees, int Reportees, bool ConnecteurIndisponible);

public static class LibellesSerialisation
{
    public static string Code(StatutSerialisation s) => s switch
    {
        StatutSerialisation.Authentique => "authentique",
        StatutSerialisation.Inconnue => "inconnue",
        StatutSerialisation.DejaDecommissionnee => "deja_decommissionnee",
        StatutSerialisation.Rappelee => "rappelee",
        _ => "expiree"
    };

    public static StatutSerialisation? DepuisCode(string? code) => code switch
    {
        "authentique" => StatutSerialisation.Authentique,
        "inconnue" => StatutSerialisation.Inconnue,
        "deja_decommissionnee" => StatutSerialisation.DejaDecommissionnee,
        "rappelee" => StatutSerialisation.Rappelee,
        "expiree" => StatutSerialisation.Expiree,
        _ => null
    };

    public static string Code(OperationSerialisation o) => o switch
    {
        OperationSerialisation.Verifier => "verifier",
        OperationSerialisation.Decommissionner => "decommissionner",
        _ => "reactiver"
    };

    public static OperationSerialisation OperationDepuisCode(string code) => code switch
    {
        "decommissionner" => OperationSerialisation.Decommissionner,
        "reactiver" => OperationSerialisation.Reactiver,
        _ => OperationSerialisation.Verifier
    };

    public static string Texte(StatutSerialisation s) => s switch
    {
        StatutSerialisation.Authentique => "authentique",
        StatutSerialisation.Inconnue => "inconnue de la base nationale",
        StatutSerialisation.DejaDecommissionnee => "déjà décommissionnée (vendue ailleurs ?)",
        StatutSerialisation.Rappelee => "rappelée",
        _ => "expirée"
    };

    public static string Texte(OperationSerialisation o) => o switch
    {
        OperationSerialisation.Verifier => "vérification à la réception",
        OperationSerialisation.Decommissionner => "décommissionnement à la vente",
        _ => "réactivation (retour ou annulation)"
    };
}

/// <summary>
/// Rejeu de la file de sérialisation (tâche de fond, et relance manuelle) : chaque opération en attente est envoyée au connecteur.
/// Acceptée : réussie. Refusée : tracée et signalée au titulaire (boîte refusée après coup : la vente est déjà faite, jamais bloquée).
/// Connecteur injoignable : l'opération reste en attente et le passage s'arrête (rejeu plus tard). Ne lève jamais d'erreur du connecteur.
/// </summary>
public sealed class TraiterFileSerialisation(IFileSerialisation file, ISerialisationConnecteur connecteur,
    Notifications.IAlertesTitulaire? alertes = null, Commun.ITraceErreurs? trace = null)
{
    public const int ParPassage = 100;

    public async Task<BilanFileSerialisation> ExecuterAsync(CancellationToken ct = default)
    {
        int reussies = 0, refusees = 0, reportees = 0;
        foreach (var op in await file.AFaireAsync(ParPassage, ct))
        {
            ReponseSerialisation reponse;
            try
            {
                reponse = op.Operation switch
                {
                    OperationSerialisation.Verifier => await connecteur.VerifierAsync(op.Boite, ct),
                    OperationSerialisation.Decommissionner => await connecteur.DecommissionnerAsync(op.Boite, ct),
                    _ => await connecteur.ReactiverAsync(op.Boite, ct)
                };
            }
            catch (Exception e) when (e is ConnecteurIndisponibleException or HttpRequestException or TimeoutException
                                      || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                // Hors ligne : cette opération et les suivantes attendent le prochain passage
                await file.ReporterAsync(op.Id, $"{connecteur.Nom} injoignable : {e.Message}", ct);
                return new BilanFileSerialisation(reussies, refusees, reportees + 1, true);
            }

            if (reponse.Acceptee)
            {
                await file.TerminerAsync(op.Id, true, reponse.Statut, reponse.Message, false, ct);
                reussies++;
                continue;
            }
            await file.TerminerAsync(op.Id, false, reponse.Statut, reponse.Message, alertes is not null, ct);
            refusees++;
            if (alertes is not null)
                await Notifications.AlerteNonEnvoyeeSignalee.SansBlocageAsync(
                    () => alertes.SurveillanceAsync(MessageAlerte(op, reponse), true, ct), $"sérialisation boîte {op.BoiteId} refusée", trace);
        }
        return new BilanFileSerialisation(reussies, refusees, reportees, false);
    }

    /// <summary>Texte de l'alerte : produit, lot, série, opération et motif ; aucune donnée de patient.</summary>
    public static string MessageAlerte(OperationEnFile op, ReponseSerialisation r) =>
        $"Sérialisation : boîte {op.Produit}{(op.Boite.Lot is { } l ? $", lot {l}" : "")}, série {op.Boite.NumeroSerie} refusée par la base nationale " +
        $"({LibellesSerialisation.Texte(r.Statut)}) lors de la {LibellesSerialisation.Texte(op.Operation)}" +
        (op.Operation == OperationSerialisation.Decommissionner ? " : la vente est déjà faite, vérifiez la boîte et le patient." : ".");
}

/// <summary>Consultation et pilotage de la file (écran Administration › Sérialisation, titulaire).</summary>
public sealed class PiloterFileSerialisation(IFileSerialisation file, TraiterFileSerialisation traiter)
{
    public static readonly string[] Statuts = ["en_attente", "reussie", "refusee"];

    public Task<EtatFileSerialisation> EtatAsync(CancellationToken ct = default) => file.EtatAsync(ct);

    public Task<(IReadOnlyList<LigneFileSerialisation> Lignes, int Total)> ListerAsync(string? statut, int page, int taille, CancellationToken ct = default)
    {
        if (statut is not null && !Statuts.Contains(statut))
            throw new RegleMetierException("Statut inconnu : en_attente, reussie ou refusee.", "statut");
        return file.ListerAsync(statut, Math.Max(1, page), Math.Clamp(taille, 1, 200), ct);
    }

    /// <summary>Relance puis rejoue aussitôt la file (le connecteur peut toujours être hors ligne : la file reste alors en attente).</summary>
    public async Task<(int Relancees, BilanFileSerialisation Bilan)> RelancerAsync(long? id, long utilisateurId, CancellationToken ct = default)
    {
        var n = await file.RelancerAsync(id, utilisateurId, ct);
        if (id is not null && n == 0)
            throw new RegleMetierException("Cette opération est déjà réussie ou n'existe plus : rechargez la file.");
        return (n, await traiter.ExecuterAsync(ct));
    }

    public Task<ParametresSerialisation> ParametresAsync(CancellationToken ct = default) => file.ParametresAsync(ct);

    public Task EnregistrerAsync(bool actif, bool facticeHorsLigne, long utilisateurId, CancellationToken ct = default) =>
        file.EnregistrerParametresAsync(actif, facticeHorsLigne, utilisateurId, ct);

    public Task<IReadOnlyList<Gs1Identifiant>> IdentifiantsGs1Async(CancellationToken ct = default) => file.IdentifiantsGs1Async(ct);
}
