using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Tresorerie;
using Pharma.Domain.Commun;
using Pharma.Domain.Financements;
using Pharma.Domain.Tresorerie;

namespace Pharma.Application.Financements;

/// <summary>
/// Prêt bancaire. CompteId : banque suivie en trésorerie (compte de type banque), null pour un prêteur hors trésorerie ; Banque : nom
/// du compte, sinon nom du prêteur. CapitalRembourse : capital des échéances payées ; ResteDu : capital
/// restant dû ; Utilise : part du prêt déjà employée (versée sur un compte, dépense, facture ou traite payée) ; Document : chemin GED.
/// Taux (annuel, %) et DureeMois : tels qu'au contrat, null si non saisis. ProchainMontant : mensualité, montant de la prochaine
/// échéance non payée, assurance comprise (ProchaineAssurance) ; null si aucune échéance ne reste à payer.
/// </summary>
public sealed record PretResume(long Id, long? CompteId, string Banque, string Reference, string? Objet, decimal Montant, DateOnly DatePret,
    string? Document, decimal CapitalRembourse, decimal ResteDu, decimal Utilise, int Echeances, int EcheancesPayees, int EchuesNonPayees,
    DateOnly? ProchaineEcheance, decimal? ProchainMontant, string Employe, decimal? Taux = null, int? DureeMois = null, decimal? ProchaineAssurance = null)
{
    public decimal AUtiliser => Montant - Utilise;
    /// <summary>Prêteur hors trésorerie : son argent n'entre dans aucun compte suivi, il ne sert que directement.</summary>
    public bool HorsTresorerie => CompteId is null;
}

/// <summary>Échéance d'un prêt ; PayeeLe / Moyen / Compte / ReferencePaiement : paiement confirmé par le titulaire, null sinon.</summary>
public sealed record EcheancePret(long Id, long PretId, string Pret, string Banque, int Rang, LigneEcheancier Ligne, DateOnly? PayeeLe = null,
    string? Moyen = null, string? Compte = null, string? ReferencePaiement = null, string? Employe = null);

/// <summary>Usage d'un prêt ou d'un apport du titulaire. Usage : tresorerie, depense, reglement (facture d'un grossiste) ou traite.</summary>
public sealed record UtilisationFinancement(long Id, string Source, long? PretId, string Usage, DateOnly Date, decimal Montant, string Libelle, string Employe);

public sealed record PretDetail(PretResume Pret, IReadOnlyList<EcheancePret> Echeances, IReadOnlyList<UtilisationFinancement> Utilisations);

/// <summary>Filtres des prêts. Etat : en_cours (défaut : capital restant dû), rembourses, tous. Texte : référence, objet ou banque.</summary>
public sealed record FiltrePrets(string Etat, string? Texte, long? CompteId)
{
    public static readonly string[] Etats = ["en_cours", "rembourses", "tous"];

    public static FiltrePrets Lire(string? etat, string? texte, long? compteId) =>
        new(Etats.Contains(etat) ? etat! : "en_cours", string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), compteId is > 0 ? compteId : null);
}

/// <summary>
/// Filtres des échéances de prêt. Etat : a_payer (défaut : non payées, échues ou dans les 7 jours), echues, a_venir, payees, toutes.
/// Texte : référence du prêt ou banque. Aujourdhui : date du serveur.
/// </summary>
public sealed record FiltreEcheances(string Etat, string? Texte, DateOnly Aujourdhui)
{
    public static readonly string[] Etats = ["a_payer", "echues", "a_venir", "payees", "toutes"];

    public static FiltreEcheances Lire(string? etat, string? texte, DateOnly aujourdhui) =>
        new(Etats.Contains(etat) ? etat! : "a_payer", string.IsNullOrWhiteSpace(texte) ? null : texte.Trim(), aujourdhui);
}

/// <summary>Ligne du compte du titulaire : Sens apport (la pharmacie lui doit) ou remboursement (il s'est remboursé).</summary>
public sealed record MouvementTitulaire(DateOnly Date, string Sens, decimal Montant, string Usage, string Libelle, string Employe);

/// <summary>Filtres du compte du titulaire : Sens (apport, remboursement ou null), période.</summary>
public sealed record FiltreCompteTitulaire(string? Sens, DateOnly? Du, DateOnly? Au)
{
    public static FiltreCompteTitulaire Lire(string? sens, DateOnly? du, DateOnly? au) => new(sens is "apport" or "remboursement" ? sens : null, du, au);
}

/// <summary>Solde dû au titulaire (apports moins remboursements), totaux, et une page de ses mouvements filtrés.</summary>
public sealed record CompteTitulaire(decimal SoldeDu, decimal TotalApports, decimal TotalRemboursements, Page<MouvementTitulaire> Mouvements);

/// <summary>Source utilisable (choix d'une liste) : Code « titulaire » ou « pret:ID » ; Disponible : reste du prêt à employer (null : apport).</summary>
public sealed record SourceDisponible(string Code, string Libelle, decimal? Disponible);

/// <summary>
/// CompteId (banque de la trésorerie) ou Preteur (nom libre, hors trésorerie) : exactement l'un des deux. Taux (annuel, %) et DureeMois
/// (mois) : facultatifs, tels qu'au contrat.
/// </summary>
public sealed record PretAEcrire(long? CompteId, string? Preteur, string Reference, string? Objet, decimal Montant, DateOnly DatePret, long UtilisateurId,
    decimal? Taux = null, int? DureeMois = null);

/// <summary>Paiement d'une échéance. PartTiroir (espèces) : argent du tiroir de la caisse ouverte porté au coffre avant le paiement.</summary>
public sealed record PaiementEcheanceAEcrire(long EcheanceId, DateOnly Date, string Moyen, long? CompteId, string? Reference, decimal PartTiroir,
    long? SessionCaisseId, long UtilisateurId);

/// <summary>Usage tresorerie (argent versé sur le compte) ou depense (dépense payée, inscrite sur le compte avec l'entrée qui la couvre).</summary>
public sealed record UtilisationAEcrire(SourceFinancement Source, string Usage, DateOnly Date, decimal Montant, long CompteId, string? Categorie,
    string Libelle, string? Reference, long UtilisateurId);

public sealed record RemboursementTitulaireAEcrire(DateOnly Date, decimal Montant, long CompteId, string Libelle, string? Reference, long UtilisateurId);

/// <summary>Aperçu d'un échéancier importé : lignes lues, n° d'origine, lignes écartées, incohérences (lecture et contrôles).</summary>
public sealed record ApercuEcheancier(IReadOnlyList<LigneEcheancier> Lignes, IReadOnlyList<int> Origines, IReadOnlyList<string> Ecartees,
    IReadOnlyList<AnomalieEcheancier> Anomalies);

public interface IFinancements
{
    Task<Page<PretResume>> PretsAsync(FiltrePrets filtre, DemandePage page, CancellationToken ct = default);
    Task<PretDetail?> PretAsync(long id, CancellationToken ct = default);
    /// <summary>Refuse une référence déjà prise pour la même banque (ou le même prêteur hors trésorerie).</summary>
    Task<long> CreerPretAsync(PretAEcrire p, CancellationToken ct = default);
    /// <summary>Nom d'un prêteur hors trésorerie corrigé : ses colonnes d'import retenues sont reportées sur le nouveau nom.</summary>
    Task ModifierPretAsync(long id, PretAEcrire p, CancellationToken ct = default);
    Task JoindreDocumentAsync(long id, string chemin, CancellationToken ct = default);
    /// <summary>Remplace les échéances non payées (les payées, en tête, restent telles quelles) par les lignes qui suivent.</summary>
    Task RemplacerEcheancierAsync(long pretId, int payees, IReadOnlyList<LigneEcheancier> lignes, CancellationToken ct = default);
    /// <summary>Colonnes retenues pour la banque de la trésorerie (<paramref name="compteId"/>), sinon pour le prêteur hors trésorerie.</summary>
    Task<CorrespondanceColonnes?> CorrespondanceAsync(long? compteId, string? preteur, CancellationToken ct = default);
    Task EnregistrerCorrespondanceAsync(long? compteId, string? preteur, CorrespondanceColonnes c, long utilisateurId, CancellationToken ct = default);
    /// <summary>Échéances selon l'état, filtrées, paginées, de la plus ancienne à la plus récente (payées : la plus récente d'abord).</summary>
    Task<Page<EcheancePret>> EcheancesAsync(FiltreEcheances filtre, DemandePage page, CancellationToken ct = default);
    Task<EcheancePret?> EcheanceAsync(long id, CancellationToken ct = default);
    /// <summary>
    /// Paiement en une transaction : échéance verrouillée (la plus ancienne non payée de son prêt), complément du tiroir porté au coffre,
    /// solde du compte relu sous verrou, capital (remboursement) et charges financières (dépense) sortis du compte.
    /// </summary>
    Task<long> PayerEcheanceAsync(PaiementEcheanceAEcrire p, CancellationToken ct = default);
    /// <summary>Entrée « financement » sur le compte (et dépense) ; prêt verrouillé : jamais plus que ce qui reste à employer.</summary>
    Task<long> UtiliserAsync(UtilisationAEcrire u, CancellationToken ct = default);
    Task<CompteTitulaire> CompteTitulaireAsync(FiltreCompteTitulaire filtre, DemandePage page, CancellationToken ct = default);
    /// <summary>Solde dû et solde du compte relus sous verrou.</summary>
    Task<long> RembourserTitulaireAsync(RemboursementTitulaireAEcrire r, CancellationToken ct = default);
    /// <summary>Prêts dont une part reste à employer.</summary>
    Task<IReadOnlyList<SourceDisponible>> SourcesAsync(CancellationToken ct = default);
}

/// <summary>Lecture d'un fichier de tableau (Excel .xlsx ou CSV) en lignes de cellules texte.</summary>
public interface ILecteurTableau
{
    IReadOnlyList<IReadOnlyList<string?>> Lire(string nomFichier, byte[] contenu);
}

/// <summary>Financements : réservés au titulaire (prêts, échéances, compte du titulaire).</summary>
public sealed class ConsulterFinancements(IFinancements financements, IPermissionService droits, IHorloge horloge)
{
    public const int JoursAvantEcheance = 7;

    public async Task<Page<PretResume>> PretsAsync(long utilisateurId, FiltrePrets filtre, DemandePage page, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les prêts", ct);
        return await financements.PretsAsync(filtre, page, ct);
    }

    public async Task<PretDetail> PretAsync(long utilisateurId, long id, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter un prêt", ct);
        return await financements.PretAsync(id, ct) ?? throw new RegleMetierException("Prêt introuvable.");
    }

    public async Task<Page<EcheancePret>> EcheancesAsync(long utilisateurId, string? etat, string? texte, DemandePage page, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter les échéances des prêts", ct);
        return await financements.EcheancesAsync(FiltreEcheances.Lire(etat, texte, horloge.Aujourdhui), page, ct);
    }

    public async Task<CompteTitulaire> CompteTitulaireAsync(long utilisateurId, FiltreCompteTitulaire filtre, DemandePage page, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter le compte du titulaire", ct);
        return await financements.CompteTitulaireAsync(filtre, page, ct);
    }

    /// <summary>Sources proposées pour payer hors trésorerie : l'apport du titulaire, puis les prêts dont une part reste à employer.</summary>
    public async Task<IReadOnlyList<SourceDisponible>> SourcesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "utiliser un prêt ou un apport", ct);
        return [new SourceDisponible(SourceFinancement.Titulaire.Code, "Apport du titulaire", null), .. await financements.SourcesAsync(ct)];
    }

    /// <summary>Colonnes retenues pour le prêteur du prêt : sa banque de la trésorerie, ou son nom s'il est hors trésorerie.</summary>
    public async Task<CorrespondanceColonnes?> CorrespondanceAsync(long utilisateurId, PretResume pret, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer un échéancier", ct);
        return await financements.CorrespondanceAsync(pret.CompteId, pret.HorsTresorerie ? pret.Banque : null, ct);
    }
}

/// <summary>
/// Création et modification d'un prêt (titulaire seul), document joint ; journalisés. Prêteur : une banque suivie en trésorerie
/// (compteId) ou un autre prêteur, hors trésorerie (nom libre) ; exactement l'un des deux (PreteurPret). Taux annuel et durée en mois :
/// facultatifs, saisis tels qu'au contrat (RegleConditionsPret), jamais recalculés ; ils se corrigent à tout moment.
/// </summary>
public sealed class EnregistrerPret(IFinancements financements, IPermissionService droits, IHorloge horloge, IJournal journal, ITresorerie tresorerie)
{
    public async Task<long> CreerAsync(long? compteId, string? preteur, string? reference, string? objet, decimal montant, DateOnly? date,
        long utilisateurId, CancellationToken ct = default, decimal? taux = null, int? dureeMois = null)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "enregistrer un prêt", ct);
        var p = await VerifierAsync(compteId, preteur, reference, objet, montant, date, utilisateurId, ct) with { Taux = taux, DureeMois = dureeMois };
        RegleConditionsPret.Verifier(taux, dureeMois);
        var id = await financements.CreerPretAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "pret_cree", "pret_bancaire", id, Details(p), null, ct);
        return id;
    }

    /// <summary>
    /// Banque suivie, type de prêteur (banque suivie ou prêteur hors trésorerie), montant et date ne changent plus dès qu'une échéance est
    /// payée ou qu'une part du prêt est employée ; le nom d'un prêteur hors trésorerie se corrige à tout moment (faute de frappe), l'ancien
    /// et le nouveau nom inscrits au journal, et ses colonnes d'import retenues suivent le nouveau nom.
    /// </summary>
    public async Task ModifierAsync(long id, long? compteId, string? preteur, string? reference, string? objet, decimal montant, DateOnly? date,
        long utilisateurId, CancellationToken ct = default, decimal? taux = null, int? dureeMois = null)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "modifier un prêt", ct);
        var avant = (await financements.PretAsync(id, ct) ?? throw new RegleMetierException("Prêt introuvable.")).Pret;
        var p = await VerifierAsync(compteId, preteur, reference, objet, montant, date, utilisateurId, ct, avant.CompteId) with { Taux = taux, DureeMois = dureeMois };
        RegleConditionsPret.Verifier(taux, dureeMois);
        // Autre banque, ou passage banque suivie ↔ prêteur hors trésorerie : CompteId différent (null pour un prêteur hors trésorerie)
        if ((avant.EcheancesPayees > 0 || avant.Utilise > 0) && (p.CompteId != avant.CompteId || p.Montant != avant.Montant || p.DatePret != avant.DatePret))
            throw new RegleMetierException("Une échéance est payée ou une part du prêt est employée : la banque, le type de prêteur, le montant "
                + "et la date ne changent plus (le nom d’un prêteur hors trésorerie, le taux et la durée se corrigent).", "montant");
        var renomme = avant.CompteId is null && p.CompteId is null && p.Preteur != avant.Banque ? avant.Banque : null;
        await financements.ModifierPretAsync(id, p, ct);
        var details = Details(p);
        if (renomme is not null) details["preteur_avant"] = renomme;
        await journal.EcrireAsync(utilisateurId, "pret_modifie", "pret_bancaire", id, details, null, ct);
    }

    public async Task JoindreDocumentAsync(long id, string? chemin, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "joindre un document à un prêt", ct);
        if (string.IsNullOrWhiteSpace(chemin)) throw new RegleMetierException("Document manquant.");
        _ = await financements.PretAsync(id, ct) ?? throw new RegleMetierException("Prêt introuvable.");
        await financements.JoindreDocumentAsync(id, chemin.Trim(), ct);
        await journal.EcrireAsync(utilisateurId, "pret_document", "pret_bancaire", id, new Dictionary<string, object?> { ["document"] = chemin.Trim() }, null, ct);
    }

    private async Task<PretAEcrire> VerifierAsync(long? compteId, string? preteur, string? reference, string? objet, decimal montant, DateOnly? date,
        long utilisateurId, CancellationToken ct, long? banqueActuelle = null)
    {
        // Banque suivie en trésorerie, ou autre prêteur (nom libre) : exactement l'un des deux
        var nomPreteur = PreteurPret.Verifier(compteId, preteur);
        // Banque de la trésorerie ; une banque devenue inactive reste admise pour un prêt qu'elle portait déjà
        if (nomPreteur is null
            && !(await tresorerie.ComptesAsync(ct)).Any(c => c.Id == compteId && c.Type == "banque" && (c.Actif || c.Id == banqueActuelle)))
            throw new RegleMetierException("Choisissez une banque active de la trésorerie (Finances, Trésorerie, Comptes et soldes), ou saisissez le nom d’un autre prêteur.", "compte");
        if (string.IsNullOrWhiteSpace(reference)) throw new RegleMetierException("Indiquez la référence du prêt (n° du contrat).", "reference");
        if (montant <= 0) throw new RegleMetierException("Le montant du prêt doit être supérieur à 0.", "montant");
        if (!Arrondi.AuCentime(montant)) throw new RegleMetierException("Le montant se saisit au centime près (2 décimales au plus).", "montant");
        var jour = date ?? horloge.Aujourdhui;
        if (jour > horloge.Aujourdhui) throw new RegleMetierException("La date du prêt ne peut pas être dans le futur.", "date");
        return new PretAEcrire(nomPreteur is null ? compteId : null, nomPreteur, reference.Trim(), string.IsNullOrWhiteSpace(objet) ? null : objet.Trim(), montant, jour, utilisateurId);
    }

    private static Dictionary<string, object?> Details(PretAEcrire p) => new()
    {
        ["compte_id"] = p.CompteId, ["preteur"] = p.Preteur, ["reference"] = p.Reference, ["objet"] = p.Objet, ["montant"] = p.Montant, ["date"] = p.DatePret,
        ["taux"] = p.Taux, ["duree_mois"] = p.DureeMois
    };
}

/// <summary>
/// Échéancier d'un prêt : contrôlé (RegleEcheancier) puis enregistré à la place des échéances non payées ; les échéances payées, en
/// tête, sont gardées telles quelles et doivent revenir identiques. Aperçu d'un tableau importé (correspondance des colonnes) et
/// correspondance retenue pour la banque. Titulaire seul ; journalisé.
/// </summary>
public sealed class EnregistrerEcheancier(IFinancements financements, IPermissionService droits, IJournal journal, ILecteurTableau? lecteur = null)
{
    public const int TailleMax = 10 * 1024 * 1024;
    public const int LignesMax = 2000;

    /// <summary>Contrôles seuls (aperçu, saisie en cours) : rien n'est enregistré.</summary>
    public async Task<IReadOnlyList<AnomalieEcheancier>> ControlerAsync(long pretId, IReadOnlyList<LigneEcheancier> lignes, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "contrôler un échéancier", ct);
        var detail = await financements.PretAsync(pretId, ct) ?? throw new RegleMetierException("Prêt introuvable.");
        return [.. ControlePayees(detail, lignes), .. RegleEcheancier.Controler(detail.Pret.Montant, detail.Pret.DatePret, lignes)];
    }

    public async Task ExecuterAsync(long pretId, IReadOnlyList<LigneEcheancier> lignes, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "enregistrer un échéancier", ct);
        var detail = await financements.PretAsync(pretId, ct) ?? throw new RegleMetierException("Prêt introuvable.");
        if (lignes.Count > LignesMax) throw new RegleMetierException($"Échéancier trop long ({LignesMax} lignes au plus).", "echeancier");
        if (ControlePayees(detail, lignes).FirstOrDefault() is { } a) throw new RegleMetierException(a.Message, "echeancier");
        RegleEcheancier.Exiger(detail.Pret.Montant, detail.Pret.DatePret, lignes);
        var payees = detail.Echeances.Count(e => e.PayeeLe is not null);
        await financements.RemplacerEcheancierAsync(pretId, payees, lignes.Skip(payees).ToList(), ct);
        var cout = CoutEcheancier.De(lignes);
        await journal.EcrireAsync(utilisateurId, "echeancier_pret", "pret_bancaire", pretId,
            new Dictionary<string, object?>
            {
                ["pret"] = detail.Pret.Reference, ["echeances"] = lignes.Count, ["gardees_payees"] = payees,
                ["interets"] = cout.Interets, ["tva"] = cout.Tva, ["marge"] = cout.Marge, ["assurance"] = cout.Assurance, ["cout_total"] = cout.CoutTotal
            }, null, ct);
    }

    /// <summary>Fichier fourni par la banque (Excel .xlsx ou CSV) lu en lignes de cellules.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<string?>>> LireFichierAsync(string? nomFichier, byte[] contenu, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer un échéancier", ct);
        if (lecteur is null) throw new InvalidOperationException("Lecteur de tableaux absent.");
        if (contenu.Length == 0) throw new RegleMetierException("Fichier vide.");
        if (contenu.Length > TailleMax) throw new RegleMetierException($"Fichier trop volumineux (au plus {TailleMax / 1024 / 1024} Mo).");
        var tableau = lecteur.Lire(nomFichier ?? "", contenu);
        if (tableau.Count == 0) throw new RegleMetierException("Aucune ligne lue dans ce fichier.");
        return tableau.Take(LignesMax).ToList();
    }

    /// <summary>Lignes de texte lues dans un PDF ou un scan (reconnaissance de texte du poste), découpées en cellules.</summary>
    public async Task<IReadOnlyList<IReadOnlyList<string?>>> LireTexteAsync(IReadOnlyList<string> lignes, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer un échéancier", ct);
        var tableau = lignes.Where(l => !string.IsNullOrWhiteSpace(l)).Take(LignesMax)
            .Select(l => (IReadOnlyList<string?>)LectureEcheancier.Cellules(l).Cast<string?>().ToList()).ToList();
        if (tableau.Count == 0) throw new RegleMetierException("Aucun texte lu dans ce document.");
        return tableau;
    }

    /// <summary>Aperçu : correspondance appliquée au tableau, lignes lues et toutes les incohérences (rien n'est enregistré).</summary>
    public async Task<ApercuEcheancier> ApercuAsync(long pretId, IReadOnlyList<IReadOnlyList<string?>> tableau, CorrespondanceColonnes correspondance,
        long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer un échéancier", ct);
        var detail = await financements.PretAsync(pretId, ct) ?? throw new RegleMetierException("Prêt introuvable.");
        var lu = LectureEcheancier.Appliquer(tableau.Take(LignesMax).ToList(), correspondance, detail.Pret.Montant);
        return new ApercuEcheancier(lu.Lignes, lu.Origines, lu.Ecartees,
            [.. lu.Illisibles, .. ControlePayees(detail, lu.Lignes), .. RegleEcheancier.Controler(detail.Pret.Montant, detail.Pret.DatePret, lu.Lignes)]);
    }

    /// <summary>
    /// Correspondance retenue pour le prêteur du prêt (sa banque de la trésorerie, ou son nom s'il est hors trésorerie) : proposée au
    /// prochain import d'un tableau de ce prêteur.
    /// </summary>
    public async Task RetenirCorrespondanceAsync(long pretId, CorrespondanceColonnes correspondance, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer un échéancier", ct);
        correspondance.Verifier();
        var detail = await financements.PretAsync(pretId, ct) ?? throw new RegleMetierException("Prêt introuvable.");
        var pret = detail.Pret;
        await financements.EnregistrerCorrespondanceAsync(pret.CompteId, pret.HorsTresorerie ? pret.Banque : null, correspondance, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "correspondance_echeancier",
            pret.HorsTresorerie ? "correspondance_echeancier_preteur" : "correspondance_echeancier", pret.CompteId,
            new Dictionary<string, object?> { ["banque"] = pret.Banque, ["hors_tresorerie"] = pret.HorsTresorerie, ["pret_id"] = pretId }, null, ct);
    }

    /// <summary>Échéances déjà payées : elles restent en tête de l'échéancier, identiques (date et montants).</summary>
    private static IEnumerable<AnomalieEcheancier> ControlePayees(PretDetail detail, IReadOnlyList<LigneEcheancier> lignes)
    {
        var payees = detail.Echeances.Where(e => e.PayeeLe is not null).OrderBy(e => e.Rang).ToList();
        for (var i = 0; i < payees.Count; i++)
        {
            var p = payees[i].Ligne;
            if (i >= lignes.Count || lignes[i] with { CapitalRestant = p.CapitalRestant } != p)
                yield return new AnomalieEcheancier(i + 1, "date",
                    $"Ligne {i + 1} : l’échéance du {p.Date:dd/MM/yyyy} est déjà payée ; elle doit rester en tête, identique ({p.Montant:N2} DA).");
        }
    }
}

/// <summary>
/// Paiement d'une échéance de prêt, confirmé par le titulaire : la plus ancienne non payée du prêt. Espèces (coffre, complété au besoin
/// par l'argent du tiroir de la caisse ouverte, porté au coffre), chèque, virement ou prélèvement (banque et référence obligatoires).
/// Le capital diminue la dette envers la banque ; intérêts, assurance, TVA et marge sont une dépense « charges financières ». Journalisé.
/// </summary>
public sealed class PayerEcheancePret(IFinancements financements, ISessionsCaisse sessions, IPermissionService droits, IHorloge horloge, IJournal journal,
    ITresorerie tresorerie)
{
    public async Task<long> ExecuterAsync(long echeanceId, DateOnly? date, string? moyen, long? compteId, string? reference, decimal partTiroir,
        long utilisateurId, string? poste, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "confirmer le paiement d’une échéance de prêt", ct);
        var e = await financements.EcheanceAsync(echeanceId, ct) ?? throw new RegleMetierException("Échéance introuvable.");
        if (e.PayeeLe is not null) throw new RegleMetierException("Cette échéance est déjà payée : rechargez la liste.");
        var jour = date ?? horloge.Aujourdhui;
        if (jour > horloge.Aujourdhui) throw new RegleMetierException("La date du paiement ne peut pas être dans le futur.", "date");
        var m = moyen is "especes" or "cheque" or "virement" or "prelevement" ? moyen
            : throw new RegleMetierException("Une échéance se paie en espèces, par chèque, par virement ou par prélèvement.", "moyen");
        if (partTiroir < 0 || !Arrondi.AuCentime(partTiroir))
            throw new RegleMetierException("Le complément pris dans le tiroir se saisit au centime près, positif.", "tiroir");
        if (m != "especes")
        {
            if (compteId is not > 0) throw new RegleMetierException("Choisissez la banque débitée.", "compte");
            if (!(await tresorerie.ComptesAsync(ct)).Any(c => c.Id == compteId && c.Type == "banque" && c.Actif))
                throw new RegleMetierException("La banque choisie n’existe plus ou n’est plus active.", "compte");
            if (string.IsNullOrWhiteSpace(reference))
                throw new RegleMetierException(m switch { "cheque" => "Indiquez le n° du chèque.", "virement" => "Indiquez la référence du virement.", _ => "Indiquez la référence du prélèvement." }, "reference");
            if (partTiroir > 0) throw new RegleMetierException("Seul un paiement en espèces peut être complété par le tiroir de la caisse.", "tiroir");
        }
        else compteId = null;
        if (partTiroir > e.Ligne.Montant) throw new RegleMetierException("Le complément pris dans le tiroir dépasse l’échéance.", "tiroir");

        long? sessionId = null;
        if (partTiroir > 0)
        {
            var session = await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct)
                          ?? throw new RegleMetierException("Ouvrez la caisse du poste pour prendre un complément dans le tiroir.", "tiroir");
            var dansTiroir = session.FondCaisse + await sessions.VentesEspecesAsync(session.Id, ct);
            if (partTiroir > dansTiroir) throw new RegleMetierException($"Le tiroir ne contient en principe que {dansTiroir:N2} DA.", "tiroir");
            sessionId = session.Id;
        }

        var id = await financements.PayerEcheanceAsync(new PaiementEcheanceAEcrire(echeanceId, jour, m, compteId,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), partTiroir, sessionId, utilisateurId), ct);
        var (capital, charges) = RegleEcheancier.Parts(e.Ligne);
        await journal.EcrireAsync(utilisateurId, "echeance_pret_payee", "echeance_pret", echeanceId,
            new Dictionary<string, object?>
            {
                ["pret"] = e.Pret, ["banque"] = e.Banque, ["echeance"] = e.Ligne.Date, ["montant"] = e.Ligne.Montant, ["capital"] = capital,
                ["charges_financieres"] = charges, ["assurance"] = e.Ligne.Assurance, ["date"] = jour, ["moyen"] = m, ["compte_id"] = compteId, ["part_tiroir"] = partTiroir
            }, null, ct);
        return id;
    }
}

/// <summary>
/// Usage d'un prêt ou d'un apport du titulaire dans la trésorerie : argent versé au coffre ou en banque, ou dépense payée (inscrite
/// sur le compte choisi avec l'entrée qui la couvre : le solde du compte ne change pas). Titulaire seul ; journalisé.
/// Les factures et traites des grossistes se paient par la source depuis Achats (règlements, traites à payer).
/// Prêt d'un prêteur hors trésorerie : son argent n'entre dans aucun compte suivi, il ne se verse pas ; il ne sert que directement.
/// </summary>
public sealed class UtiliserFinancement(IFinancements financements, IPermissionService droits, IHorloge horloge, IJournal journal, ITresorerie tresorerie,
    Referentiels.MotifsActifs? motifs = null)
{
    public async Task<long> ExecuterAsync(string? source, string? usage, DateOnly? date, decimal montant, long compteId, string? categorie, string? libelle,
        string? reference, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "utiliser un prêt ou un apport", ct);
        var s = SourceFinancement.Lire(source) ?? throw new RegleMetierException("Choisissez la source : un prêt ou l’apport du titulaire.", "source");
        if (usage is not ("tresorerie" or "depense")) throw new RegleMetierException("Usage inconnu : versement sur un compte ou dépense payée.", "usage");
        if (usage == "tresorerie" && s.PretId is { } pretId && (await financements.PretAsync(pretId, ct))?.Pret is { HorsTresorerie: true } horsTresorerie)
            throw new RegleMetierException($"Le prêt {horsTresorerie.Reference} vient d’un prêteur hors trésorerie ({horsTresorerie.Banque}) : son argent n’entre "
                + "dans aucun compte suivi. Employez-le directement : facture ou traite d’un grossiste (Achats), ou dépense payée.", "usage");
        var compte = (await tresorerie.ComptesAsync(ct)).FirstOrDefault(c => c.Id == compteId && c.Actif)
                     ?? throw new RegleMetierException("Compte introuvable ou inactif.", "compte");
        var jour = date ?? horloge.Aujourdhui;
        RegleTresorerie.VerifierMontantEtDate(montant, jour, horloge.Aujourdhui, compte.DateInitiale);
        if (usage == "depense")
        {
            // Types de charges réglables (script 094) : catégories actives ; à défaut, la liste de départ
            var actives = motifs is null ? RegleTresorerie.Categories.Select(c => c.Code).ToList()
                : (await motifs.ExecuterAsync(Pharma.Domain.Referentiels.RegleMotifs.Charge, ct)).Select(m => m.Code).ToList();
            if (categorie == RegleTresorerie.ChargesFinancieres || !actives.Contains(categorie ?? ""))
                throw new RegleMetierException("Choisissez la catégorie de la dépense.", "categorie");
            if (string.IsNullOrWhiteSpace(libelle)) throw new RegleMetierException("Indiquez le libellé de la dépense.", "libelle");
        }
        else categorie = null;
        var texte = string.IsNullOrWhiteSpace(libelle) ? (s.EstTitulaire ? "Apport du titulaire" : "Versement du prêt") : libelle.Trim();
        var id = await financements.UtiliserAsync(new UtilisationAEcrire(s, usage, jour, montant, compteId, categorie, texte,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "financement_utilise", "utilisation_financement", id,
            new Dictionary<string, object?>
            {
                ["source"] = s.Type, ["pret_id"] = s.PretId, ["usage"] = usage, ["montant"] = montant, ["compte"] = compte.Nom, ["categorie"] = categorie,
                ["libelle"] = texte
            }, null, ct);
        return id;
    }
}

/// <summary>Le titulaire se rembourse ses apports : sortie du coffre ou d'une banque, au plus ce qui lui est dû. Journalisé.</summary>
public sealed class RembourserTitulaire(IFinancements financements, IPermissionService droits, IHorloge horloge, IJournal journal, ITresorerie tresorerie)
{
    public async Task<long> ExecuterAsync(DateOnly? date, decimal montant, long compteId, string? libelle, string? reference, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "rembourser le titulaire", ct);
        var compte = (await tresorerie.ComptesAsync(ct)).FirstOrDefault(c => c.Id == compteId && c.Actif)
                     ?? throw new RegleMetierException("Compte introuvable ou inactif.", "compte");
        var jour = date ?? horloge.Aujourdhui;
        RegleTresorerie.VerifierMontantEtDate(montant, jour, horloge.Aujourdhui, compte.DateInitiale);
        var du = (await financements.CompteTitulaireAsync(new FiltreCompteTitulaire(null, null, null), new DemandePage(1, DemandePage.TailleMin), ct)).SoldeDu;
        RegleCompteTitulaire.VerifierRemboursement(montant, du);
        if (compte.Solde < montant) throw new RegleMetierException($"Solde insuffisant sur {compte.Nom} ({compte.Solde:N2} DA).", "montant");
        var texte = string.IsNullOrWhiteSpace(libelle) ? "Remboursement du titulaire" : libelle.Trim();
        var id = await financements.RembourserTitulaireAsync(new RemboursementTitulaireAEcrire(jour, montant, compteId, texte,
            string.IsNullOrWhiteSpace(reference) ? null : reference.Trim(), utilisateurId), ct);
        await journal.EcrireAsync(utilisateurId, "titulaire_rembourse", "remboursement_titulaire", id,
            new Dictionary<string, object?> { ["montant"] = montant, ["compte"] = compte.Nom, ["du_avant"] = du }, null, ct);
        return id;
    }
}
