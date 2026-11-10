using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;

namespace Pharma.Application.Reprise;

// ---- Modèles -------------------------------------------------------------------

/// <summary>Source trouvée sur le PC serveur. Origine : dsn (source ODBC déclarée) ou fichier (base trouvée dans un dossier).</summary>
public sealed record SourceDisponible(string Libelle, string? ChaineConnexion, string Origine, string? Remarque,
    string? Pilote = null, string? Emplacement = null, string? Dsn = null);

public sealed record InfoSource(string Pilote, string? Version, int NombreTables);

/// <summary>Tables de l'ancienne base (exemples masqués pour les données personnelles) et correspondance proposée.</summary>
public sealed record ResultatScan(InfoSource Info, IReadOnlyList<TableSource> Tables, IReadOnlyList<PropositionReprise> Propositions);

/// <summary>Correspondance confirmée par l'utilisateur : table de l'ancienne base et champ de reprise → colonne.</summary>
public sealed record CorrespondanceTable(CibleReprise Cible, string Table, IReadOnlyDictionary<string, string> Colonnes);

public sealed record ResultatExtraction(long SessionId, IReadOnlyDictionary<CibleReprise, int> Lignes, int Doublons, int Bloquants);

public sealed record SessionReprise(long Id, string Logiciel, DateTime Debut, string Statut);

/// <summary>Produit de l'ancien logiciel sans correspondance sûre : proposé par ressemblance de nom, ou sans proposition.</summary>
public sealed record ProduitAConfirmer(string RefSource, string Nom, string? Dosage, string? CodeBarre, long? ProduitPropose, string? NomPropose, decimal? Score);

public sealed record AnomalieReprise(string Entite, string? RefSource, string Gravite, string Message);
public sealed record LigneVerification(string Indicateur, decimal Ancien, decimal Nouveau, decimal Ecart);

/// <summary>Rapprochement : nombre de produits par méthode (code_barre, num_enregistrement, nom_approche, manuel, nouveau, aucune).</summary>
public sealed record EtatReprise(SessionReprise Session, IReadOnlyDictionary<string, int> Lignes, IReadOnlyDictionary<string, int> Rapprochement,
    IReadOnlyList<ProduitAConfirmer> AConfirmer, IReadOnlyList<AnomalieReprise> Anomalies, IReadOnlyList<LigneVerification> Verification);

public enum DecisionProduit { Confirmer, Autre, Creer }

public interface ISourceReprise
{
    /// <summary>Sources ODBC déclarées sur le PC serveur, et bases trouvées dans le dossier indiqué (s'il y en a un).</summary>
    Task<IReadOnlyList<SourceDisponible>> SourcesAsync(string? dossier, CancellationToken ct = default);
    /// <summary>Pilotes ODBC installés sur le PC serveur (64 bits).</summary>
    IReadOnlyList<string> Pilotes();
    Task<InfoSource> TesterAsync(string chaineConnexion, CancellationToken ct = default);
    Task<IReadOnlyList<TableSource>> ScannerAsync(string chaineConnexion, int exemples, CancellationToken ct = default);
    /// <summary>Lit toutes les lignes des colonnes demandées (vérifiées dans la structure de la table), en lecture seule.</summary>
    IAsyncEnumerable<string?[]> LireAsync(string chaineConnexion, string table, IReadOnlyList<string> colonnes, CancellationToken ct = default);
}

public interface IRepriseImport
{
    Task<long> CreerSessionAsync(string logiciel, CancellationToken ct = default);
    /// <summary>Insère dans la table de transit de la cible ; renvoie les lignes insérées et les doublons d'identifiant ignorés.</summary>
    Task<(int Inserees, int Doublons)> InsererAsync(long session, CibleReprise cible, IReadOnlyList<string> champs,
        IAsyncEnumerable<string?[]> lignes, CancellationToken ct = default);
    /// <summary>Rapprochement des produits puis contrôles ; renvoie le nombre d'anomalies bloquantes.</summary>
    Task<int> RapprocherEtControlerAsync(long session, CancellationToken ct = default);
    Task<int> ControlerAsync(long session, CancellationToken ct = default);
    Task<IReadOnlyList<SessionReprise>> SessionsAsync(CancellationToken ct = default);
    Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default);
    Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default);
    Task ChargerAsync(long session, long utilisateurId, CancellationToken ct = default);
    Task AnnulerAsync(long session, CancellationToken ct = default);
}

// ---- Cas d'usage (titulaire seulement) ---------------------------------------------

public sealed class ListerSourcesReprise(ISourceReprise source, IPermissionService droits)
{
    public async Task<IReadOnlyList<string>> PilotesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "préparer la reprise de données", ct);
        return source.Pilotes();
    }

    public async Task<IReadOnlyList<SourceDisponible>> ExecuterAsync(string? dossier, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "préparer la reprise de données", ct);
        return await source.SourcesAsync(string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim(), ct);
    }
}

/// <summary>Connexion en lecture seule à l'ancienne base, puis scan de sa structure et correspondance proposée.</summary>
public sealed class ScannerSourceReprise(ISourceReprise source, IPermissionService droits)
{
    public const int Exemples = 8;
    public const string Masque = "•••";

    public async Task<ResultatScan> ExecuterAsync(string? chaineConnexion, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "préparer la reprise de données", ct);
        if (string.IsNullOrWhiteSpace(chaineConnexion)) throw new RegleMetierException("Choisissez une source ou saisissez la chaîne de connexion.");
        var info = await source.TesterAsync(chaineConnexion, ct);
        var tables = await source.ScannerAsync(chaineConnexion, Exemples, ct);
        // La correspondance se calcule sur les vraies valeurs ; l'écran ne reçoit que des exemples masqués
        var propositions = DetectionReprise.Proposer(tables);
        var masquees = tables.Select(t => t with
        {
            Colonnes = t.Colonnes.Select(c => DetectionReprise.EstPersonnelle(t.Nom, c.Nom)
                ? c with { Exemples = c.Exemples.Select(v => string.IsNullOrWhiteSpace(v) ? v : Masque).ToList() }
                : c).ToList()
        }).ToList();
        return new ResultatScan(info with { NombreTables = tables.Count }, masquees, propositions);
    }
}

/// <summary>Copie les tables choisies vers les tables de transit, puis rapproche les produits et contrôle.</summary>
public sealed class ExtraireReprise(ISourceReprise source, IRepriseImport import, IPermissionService droits, IJournal journal)
{
    public async Task<ResultatExtraction> ExecuterAsync(string? chaineConnexion, string? logiciel, IReadOnlyList<CorrespondanceTable> correspondances,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "faire la reprise de données", ct);
        if (string.IsNullOrWhiteSpace(chaineConnexion)) throw new RegleMetierException("Connexion à l’ancienne base manquante.");
        if (string.IsNullOrWhiteSpace(logiciel)) throw new RegleMetierException("Indiquez le nom et la version de l’ancien logiciel (pour le rapport de reprise).");
        if (!correspondances.Any(c => c.Cible == CibleReprise.Produits))
            throw new RegleMetierException("La table des produits est indispensable.");
        foreach (var c in correspondances)
        {
            var manquants = DetectionReprise.Champs[c.Cible].Where(f => f.Obligatoire && !c.Colonnes.ContainsKey(f.Code)).Select(f => f.Libelle).ToList();
            if (manquants.Count > 0)
                throw new RegleMetierException($"{Libelle(c.Cible)} : choisissez la colonne « {string.Join(" », « ", manquants)} ».");
        }
        if (correspondances.GroupBy(c => c.Cible).Any(g => g.Count() > 1))
            throw new RegleMetierException("Une seule table par type de données.");

        var session = await import.CreerSessionAsync(logiciel.Trim(), ct);
        var lignes = new Dictionary<CibleReprise, int>();
        var doublons = 0;
        foreach (var c in correspondances.OrderBy(c => c.Cible))
        {
            var champs = c.Colonnes.Keys.ToList();
            var (n, d) = await import.InsererAsync(session, c.Cible, champs,
                source.LireAsync(chaineConnexion, c.Table, champs.Select(f => c.Colonnes[f]).ToList(), ct), ct);
            (lignes[c.Cible], doublons) = (n, doublons + d);
        }
        var bloquants = await import.RapprocherEtControlerAsync(session, ct);

        await journal.EcrireAsync(utilisateurId, "reprise_extraction", "import.session", session,
            new Dictionary<string, object?>
            {
                ["logiciel"] = logiciel.Trim(), ["produits"] = lignes.GetValueOrDefault(CibleReprise.Produits),
                ["stock"] = lignes.GetValueOrDefault(CibleReprise.Stock), ["grossistes"] = lignes.GetValueOrDefault(CibleReprise.Fournisseurs),
                ["clients"] = lignes.GetValueOrDefault(CibleReprise.Clients), ["bloquants"] = bloquants
            }, null, ct);
        return new ResultatExtraction(session, lignes, doublons, bloquants);
    }

    public static string Libelle(CibleReprise c) => c switch
    {
        CibleReprise.Produits => "Produits", CibleReprise.Stock => "Stock par lot", CibleReprise.Fournisseurs => "Grossistes", _ => "Clients et crédits"
    };
}

public sealed class ObtenirEtatReprise(IRepriseImport import, IPermissionService droits)
{
    public async Task<IReadOnlyList<SessionReprise>> SessionsAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la reprise de données", ct);
        return await import.SessionsAsync(ct);
    }

    public async Task<EtatReprise?> ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la reprise de données", ct);
        return await import.EtatAsync(session, ct);
    }
}

/// <summary>Produit à confirmer : accepter la proposition, choisir un autre produit du catalogue, ou le créer au chargement.</summary>
public sealed class DeciderProduitReprise(IRepriseImport import, IPermissionService droits)
{
    public async Task<int> ExecuterAsync(long session, string refSource, DecisionProduit decision, long? produitId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "faire la reprise de données", ct);
        if (decision == DecisionProduit.Autre && produitId is null) throw new RegleMetierException("Choisissez le produit du catalogue.");
        var etat = await import.EtatAsync(session, ct) ?? throw new RegleMetierException("Session de reprise introuvable.");
        if (etat.Session.Statut != "en_cours") throw new RegleMetierException("Cette reprise est déjà chargée ou annulée.");
        await import.DeciderAsync(session, refSource, decision, produitId, ct);
        return await import.ControlerAsync(session, ct);
    }
}

/// <summary>Chargement définitif en une transaction : refusé tant qu'il reste une anomalie bloquante.</summary>
public sealed class ChargerReprise(IRepriseImport import, IPermissionService droits, IJournal journal)
{
    public async Task<EtatReprise> ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "charger la reprise de données", ct);
        var bloquants = await import.ControlerAsync(session, ct);
        if (bloquants > 0) throw new RegleMetierException($"{bloquants} anomalie(s) bloquante(s) à traiter avant le chargement.");
        await import.ChargerAsync(session, utilisateurId, ct);
        var etat = (await import.EtatAsync(session, ct))!;
        await journal.EcrireAsync(utilisateurId, "reprise_chargement", "import.session", session,
            new Dictionary<string, object?> { ["logiciel"] = etat.Session.Logiciel, ["verification"] = etat.Verification.Select(v => $"{v.Indicateur} : {v.Ancien} / {v.Nouveau}").ToList() },
            null, ct);
        return etat;
    }
}

public sealed class AnnulerReprise(IRepriseImport import, IPermissionService droits)
{
    public async Task ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "annuler la reprise de données", ct);
        await import.AnnulerAsync(session, ct);
    }
}
