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

/// <summary>Tables de l'ancienne base (exemples masqués pour les données personnelles) et correspondance proposée.
/// ExigerProduitsHistorique : option du titulaire (fichier des produits exigé pour l'historique des organismes seul), rappelée à l'écran.</summary>
public sealed record ResultatScan(InfoSource Info, IReadOnlyList<TableSource> Tables, IReadOnlyList<PropositionReprise> Propositions,
    bool ExigerProduitsHistorique = true);

/// <summary>
/// Fichier des produits (règle 2026-11-16) : toujours exigé dès que l'import contient autre chose que l'historique des organismes
/// (stock, lots, ventes, patients, crédits, grossistes) ; pour l'historique des organismes seul (Chifa et/ou MDN), selon UNE option
/// réglée par le titulaire (Administration › Caisse et contrôle › Règles, par défaut : exigé).
/// </summary>
public static class ExigenceProduitsReprise
{
    public const string Option = "Reprise de données : exiger le fichier des produits pour importer l’historique des organismes seul (Chifa, MDN)";

    /// <summary>Historiques en lecture seule : factures Chifa (CNAS, CASNOS) et MDN.</summary>
    public static bool EstHistorique(CibleReprise c) => c is CibleReprise.HistoriqueChifa or CibleReprise.HistoriqueMdn;

    public static string Information(bool exiger) => exiger
        ? "Le titulaire exige le fichier des produits pour un import de l’historique des organismes seul (Chifa, MDN)."
        : "Le titulaire n’exige pas le fichier des produits pour un import de l’historique des organismes seul (Chifa, MDN).";

    /// <summary>Refuse (RegleMetierException) un import sans la table des produits, sauf historique des organismes seul quand l'option ne l'exige pas.</summary>
    public static void Verifier(IReadOnlyCollection<CibleReprise> cibles, bool exiger)
    {
        if (cibles.Contains(CibleReprise.Produits)) return;
        if (cibles.Count == 0)
            throw new RegleMetierException("Choisissez au moins une table à reprendre (la table des produits est indispensable).");
        if (cibles.Any(c => !EstHistorique(c)))
            throw new RegleMetierException("La table des produits est indispensable dès que l’import contient du stock, des lots, des ventes, " +
                "des patients, des crédits ou des grossistes.");
        if (exiger)
            throw new RegleMetierException($"La table des produits est indispensable : {Information(true)} " +
                $"Option « {Option} » (Administration › Caisse et contrôle › Règles), modifiable par le titulaire " +
                "ou un employé qui a le droit de modifier les paramètres.");
    }
}

/// <summary>Correspondance confirmée par l'utilisateur : table de l'ancienne base et champ de reprise → colonne.</summary>
public sealed record CorrespondanceTable(CibleReprise Cible, string Table, IReadOnlyDictionary<string, string> Colonnes);

public sealed record ResultatExtraction(long SessionId, IReadOnlyDictionary<CibleReprise, int> Lignes, int Doublons, int Bloquants);

/// <summary>LancePar : qui a lancé l'extraction ; ChargeLe, ChargePar : chargement définitif (date et auteur).</summary>
public sealed record SessionReprise(long Id, string Logiciel, DateTime Debut, string Statut,
    string? LancePar = null, DateTime? ChargeLe = null, string? ChargePar = null);

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
    Task<long> CreerSessionAsync(string logiciel, string lancePar, CancellationToken ct = default);
    /// <summary>Insère dans la table de transit de la cible ; renvoie les lignes insérées et les doublons d'identifiant ignorés.</summary>
    Task<(int Inserees, int Doublons)> InsererAsync(long session, CibleReprise cible, IReadOnlyList<string> champs,
        IAsyncEnumerable<string?[]> lignes, CancellationToken ct = default);
    /// <summary>Rapprochement des produits puis contrôles ; renvoie le nombre d'anomalies bloquantes.</summary>
    Task<int> RapprocherEtControlerAsync(long session, CancellationToken ct = default);
    Task<int> ControlerAsync(long session, CancellationToken ct = default);
    Task<IReadOnlyList<SessionReprise>> SessionsAsync(CancellationToken ct = default);
    Task<EtatReprise?> EtatAsync(long session, CancellationToken ct = default);
    Task DeciderAsync(long session, string refSource, DecisionProduit decision, long? produitId, CancellationToken ct = default);
    /// <summary>Chargement attribué à utilisateurId (titulaire) ; la session note la date et chargePar.</summary>
    Task ChargerAsync(long session, long utilisateurId, string chargePar, CancellationToken ct = default);
    Task AnnulerAsync(long session, CancellationToken ct = default);
}

// ---- Cas d'usage (super admin seulement ; données chargées attribuées au titulaire) ---------------------------------------------

public sealed class ListerSourcesReprise(ISourceReprise source, SuperAdmin.SuperAdminCourant superAdmin)
{
    public async Task<IReadOnlyList<string>> PilotesAsync(long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("préparer la reprise de données");
        return source.Pilotes();
    }

    public async Task<IReadOnlyList<SourceDisponible>> ExecuterAsync(string? dossier, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("préparer la reprise de données");
        return await source.SourcesAsync(string.IsNullOrWhiteSpace(dossier) ? null : dossier.Trim(), ct);
    }
}

/// <summary>Connexion en lecture seule à l'ancienne base, puis scan de sa structure et correspondance proposée.</summary>
public sealed class ScannerSourceReprise(ISourceReprise source, SuperAdmin.SuperAdminCourant superAdmin, Caisse.ISessionsCaisse? caisse = null)
{
    public const int Exemples = 8;
    public const string Masque = "•••";

    public async Task<ResultatScan> ExecuterAsync(string? chaineConnexion, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("préparer la reprise de données");
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
        var exiger = caisse is null || (await caisse.ParametresAsync(ct)).ExigerProduitsRepriseHistorique;
        return new ResultatScan(info with { NombreTables = tables.Count }, masquees, propositions, exiger);
    }
}

/// <summary>Copie les tables choisies vers les tables de transit, puis rapproche les produits et contrôle.</summary>
public sealed class ExtraireReprise(ISourceReprise source, IRepriseImport import, SuperAdmin.SuperAdminCourant superAdmin, IJournal journal,
    Caisse.ISessionsCaisse? caisse = null)
{
    public Task<ResultatExtraction> ExecuterAsync(string? chaineConnexion, string? logiciel, IReadOnlyList<CorrespondanceTable> correspondances,
        long utilisateurId, CancellationToken ct = default) =>
        ExecuterAsync(chaineConnexion, logiciel, correspondances, TransformationsReprise.Aucune, null, utilisateurId, ct);

    /// <param name="transformations">Transformations du profil de reprise choisi (séparateur décimal, format de date, codes de classe).</param>
    /// <param name="profil">Profil utilisé (journal), null sans profil.</param>
    public async Task<ResultatExtraction> ExecuterAsync(string? chaineConnexion, string? logiciel, IReadOnlyList<CorrespondanceTable> correspondances,
        TransformationsReprise transformations, string? profil, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.ExigerValidation(SuperAdmin.ActionsSuperAdmin.RepriseExtraire, "extraire les données de l’ancien logiciel");
        if (string.IsNullOrWhiteSpace(chaineConnexion)) throw new RegleMetierException("Connexion à l’ancienne base manquante.");
        if (string.IsNullOrWhiteSpace(logiciel)) throw new RegleMetierException("Indiquez le nom et la version de l’ancien logiciel (pour le rapport de reprise).");
        // Option du titulaire (par défaut : exigé) ; toujours exigé hors historique Chifa seul
        var exigerProduits = caisse is null || (await caisse.ParametresAsync(ct)).ExigerProduitsRepriseHistorique;
        ExigenceProduitsReprise.Verifier(correspondances.Select(c => c.Cible).ToList(), exigerProduits);
        foreach (var c in correspondances)
        {
            var manquants = DetectionReprise.Champs[c.Cible].Where(f => f.Obligatoire && !c.Colonnes.ContainsKey(f.Code)).Select(f => f.Libelle).ToList();
            if (manquants.Count > 0)
                throw new RegleMetierException($"{Libelle(c.Cible)} : choisissez la colonne « {string.Join(" », « ", manquants)} ».");
        }
        if (correspondances.GroupBy(c => c.Cible).Any(g => g.Count() > 1))
            throw new RegleMetierException("Une seule table par type de données.");

        var session = await import.CreerSessionAsync(logiciel.Trim(), superAdmin.Auteur, ct);
        var lignes = new Dictionary<CibleReprise, int>();
        var doublons = 0;
        foreach (var c in correspondances.OrderBy(c => c.Cible))
        {
            var champs = c.Colonnes.Keys.ToList();
            var lues = source.LireAsync(chaineConnexion, c.Table, champs.Select(f => c.Colonnes[f]).ToList(), ct);
            if (!transformations.EstVide)
                lues = Transformer(lues, champs.Select(f => DetectionReprise.Champs[c.Cible].First(x => x.Code == f)).ToArray(), transformations);
            var (n, d) = await import.InsererAsync(session, c.Cible, champs, lues, ct);
            (lignes[c.Cible], doublons) = (n, doublons + d);
        }
        var bloquants = await import.RapprocherEtControlerAsync(session, ct);

        var details = superAdmin.Trace();
        details["logiciel"] = logiciel.Trim();
        details["produits"] = lignes.GetValueOrDefault(CibleReprise.Produits);
        details["stock"] = lignes.GetValueOrDefault(CibleReprise.Stock);
        details["grossistes"] = lignes.GetValueOrDefault(CibleReprise.Fournisseurs);
        details["clients"] = lignes.GetValueOrDefault(CibleReprise.Clients);
        details["historique_chifa"] = lignes.GetValueOrDefault(CibleReprise.HistoriqueChifa);
        details["historique_mdn"] = lignes.GetValueOrDefault(CibleReprise.HistoriqueMdn);
        details["produits_exiges_historique_organismes"] = exigerProduits;
        if (profil is not null) details["profil"] = profil;
        details["bloquants"] = bloquants;
        await journal.EcrireAsync(utilisateurId, "reprise_extraction", "import.session", session, details, null, ct);
        return new ResultatExtraction(session, lignes, doublons, bloquants);
    }

    public static string Libelle(CibleReprise c) => c switch
    {
        CibleReprise.Produits => "Produits", CibleReprise.Stock => "Stock par lot", CibleReprise.Fournisseurs => "Grossistes",
        CibleReprise.HistoriqueChifa => "Historique des ventes Chifa", CibleReprise.HistoriqueMdn => "Historique des factures MDN",
        _ => "Clients et crédits"
    };

    /// <summary>Valeurs transformées selon le profil (une valeur illisible reste telle quelle : les contrôles la signalent).</summary>
    private static async IAsyncEnumerable<string?[]> Transformer(IAsyncEnumerable<string?[]> lignes, ChampCible[] champs, TransformationsReprise t)
    {
        await foreach (var l in lignes)
        {
            for (var i = 0; i < l.Length && i < champs.Length; i++) l[i] = ProfilsReprise.Transformer(champs[i], l[i], t);
            yield return l;
        }
    }
}

public sealed class ObtenirEtatReprise(IRepriseImport import, SuperAdmin.SuperAdminCourant superAdmin)
{
    public async Task<IReadOnlyList<SessionReprise>> SessionsAsync(long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("consulter la reprise de données");
        return await import.SessionsAsync(ct);
    }

    public async Task<EtatReprise?> ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("consulter la reprise de données");
        return await import.EtatAsync(session, ct);
    }
}

/// <summary>Produit à confirmer : accepter la proposition, choisir un autre produit du catalogue, ou le créer au chargement.</summary>
public sealed class DeciderProduitReprise(IRepriseImport import, SuperAdmin.SuperAdminCourant superAdmin)
{
    public async Task<int> ExecuterAsync(long session, string refSource, DecisionProduit decision, long? produitId, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("faire la reprise de données");
        // Produit inexistant : refusé par le dépôt (« Produit introuvable dans le catalogue »)
        if (decision == DecisionProduit.Autre && produitId is not > 0) throw new RegleMetierException("Choisissez le produit du catalogue.");
        var etat = await import.EtatAsync(session, ct) ?? throw new RegleMetierException("Session de reprise introuvable.");
        if (etat.Session.Statut != "en_cours") throw new RegleMetierException("Cette reprise est déjà chargée ou annulée.");
        await import.DeciderAsync(session, refSource, decision, produitId, ct);
        return await import.ControlerAsync(session, ct);
    }
}

/// <summary>Chargement définitif en une transaction : refusé tant qu'il reste une anomalie bloquante.</summary>
public sealed class ChargerReprise(IRepriseImport import, SuperAdmin.SuperAdminCourant superAdmin, IJournal journal)
{
    public async Task<EtatReprise> ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.ExigerValidation(SuperAdmin.ActionsSuperAdmin.RepriseCharger, "charger la reprise de données");
        var bloquants = await import.ControlerAsync(session, ct);
        if (bloquants > 0) throw new RegleMetierException($"{bloquants} anomalie(s) bloquante(s) à traiter avant le chargement.");
        // Données attribuées au titulaire (utilisateurId) ; la session garde qui a lancé le chargement et quand
        await import.ChargerAsync(session, utilisateurId, superAdmin.Auteur, ct);
        var etat = (await import.EtatAsync(session, ct))!;
        var details = superAdmin.Trace();
        details["logiciel"] = etat.Session.Logiciel;
        details["verification"] = etat.Verification.Select(v => $"{v.Indicateur} : {v.Ancien} / {v.Nouveau}").ToList();
        await journal.EcrireAsync(utilisateurId, "reprise_chargement", "import.session", session, details, null, ct);
        return etat;
    }
}

public sealed class AnnulerReprise(IRepriseImport import, SuperAdmin.SuperAdminCourant superAdmin, IJournal journal)
{
    public async Task ExecuterAsync(long session, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.ExigerValidation(SuperAdmin.ActionsSuperAdmin.RepriseAnnuler, "annuler la reprise de données");
        var etat = await import.EtatAsync(session, ct) ?? throw new RegleMetierException("Session de reprise introuvable.");
        if (etat.Session.Statut != "en_cours") throw new RegleMetierException("Cette reprise est déjà chargée ou annulée : rien n’a été modifié.");
        await import.AnnulerAsync(session, ct);
        await journal.EcrireAsync(utilisateurId, "reprise_annulation", "import.session", session, superAdmin.Trace(), null, ct);
    }
}
