using System.Globalization;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Psychotropes;

// ---- Registre des psychotropes renforcé (lot 8, script 117) ----
// Journal chaîné du registre (journal_registre_psychotrope), annulations d'écriture, alertes paramétrables avec forçage tracé,
// suivi des classes (antibiotiques), connecteur du répertoire national des ordonnances (factice), scellement mensuel.
// Aucun nom de patient dans les messages d'alerte ni dans les journaux applicatifs (règle du lot 1).

public static class CodesAlertePsychotrope
{
    public const string PatientRepete = Pharma.Domain.Reglages.RegleAlertesPsychotropes.PatientRepete;
    public const string OrdonnanceReutilisee = Pharma.Domain.Reglages.RegleAlertesPsychotropes.OrdonnanceReutilisee;
    public const string QuantiteAnormale = Pharma.Domain.Reglages.RegleAlertesPsychotropes.QuantiteAnormale;
    public const string PrescripteurInhabituel = Pharma.Domain.Reglages.RegleAlertesPsychotropes.PrescripteurInhabituel;
    /// <summary>Wilaya du médecin connue et hors de la zone de l'officine (script 118).</summary>
    public const string PrescripteurHorsZone = Pharma.Domain.Reglages.RegleAlertesPsychotropes.PrescripteurHorsZone;
    /// <summary>Patient encore sous traitement par un psychotrope délivré avant (script 153).</summary>
    public const string TraitementEnCours = Pharma.Domain.Reglages.RegleAlertesPsychotropes.TraitementEnCours;
    public static readonly IReadOnlySet<string> Tous = Pharma.Domain.Reglages.RegleAlertesPsychotropes.Codes;
}

/// <summary>
/// Règle d'alerte (regle_alerte_psychotrope) : seuil et période en données, réglés par le titulaire.
/// UnitesParJour (quantité anormale, script 118) : unités par jour de traitement au-delà desquelles boîtes × contenance alerte ;
/// vide : contrôle inactif. PeriodeJours vide (script 153) : « sans limite » (ordonnance réutilisée) ou sans objet (traitement en cours).
/// </summary>
public sealed record RegleAlertePsychotrope(string Code, string Libelle, bool Active, int Seuil, int? PeriodeJours, bool ExigerValideur,
    int? UnitesParJour = null);

/// <summary>Traitement psychotrope encore en cours pour le patient : délivrance la plus tardive à finir (date + durée prescrite).</summary>
public sealed record TraitementPsychotropeEnCours(DateOnly Delivree, int DureeJours, DateOnly Fin);

public sealed record LignePsychotrope(long ProduitId, int Quantite);

/// <summary>Délivrance à contrôler : patient, prescripteur, n° d'ordonnance, produits sous registre (et durée de traitement si connue).</summary>
public sealed record DelivrancePsychotrope(long? PatientId, long? MedecinId, string? NumeroOrdonnance, DateOnly? DatePrescription,
    IReadOnlyList<LignePsychotrope> Lignes, int? DureeTraitementJours = null);

/// <summary>
/// Faits lus en base, chacun sur la période de sa règle. Script 118 : wilaya du médecin (null : à renseigner), wilayas de la zone de
/// l'officine (vide : pas de contrôle hors zone), contenance des produits (unités par boîte, seulement celles qui sont connues).
/// </summary>
public sealed record HistoriquePsychotrope(int DelivrancesPatient, int UsagesOrdonnance, int DelivrancesPrescripteur,
    string? WilayaMedecin = null, IReadOnlySet<string>? Zone = null, IReadOnlyDictionary<long, int>? Contenances = null,
    TraitementPsychotropeEnCours? EnCours = null);   // script 153 : traitement du patient encore en cours (null : aucun, ou durées non saisies)

public sealed record AlertePsychotrope(string Code, string Libelle, string Message, bool ExigeValideur);

/// <summary>
/// Résultat du contrôle : alertes (avertissement), réponse du répertoire national (jamais bloquante) et avis non bloquants
/// (ex. « wilaya du prescripteur à renseigner »).
/// </summary>
public sealed record ControleDelivrance(IReadOnlyList<AlertePsychotrope> Alertes, string? Repertoire, IReadOnlyList<string>? Avis = null);

public static class EvaluationAlertesPsychotropes
{
    /// <summary>Règles actives appliquées aux faits : une alerte par règle déclenchée (messages sans nom de patient).</summary>
    public static IReadOnlyList<AlertePsychotrope> Evaluer(IReadOnlyList<RegleAlertePsychotrope> regles, DelivrancePsychotrope d,
        HistoriquePsychotrope h, IReadOnlyDictionary<long, string> produits)
    {
        var alertes = new List<AlertePsychotrope>();
        foreach (var r in regles.Where(r => r.Active))
        {
            string? message = r.Code switch
            {
                CodesAlertePsychotrope.PatientRepete when d.PatientId is not null && h.DelivrancesPatient >= r.Seuil =>
                    $"Patient déjà servi {h.DelivrancesPatient} fois en psychotropes sur {r.PeriodeJours} jours (seuil {r.Seuil}).",
                CodesAlertePsychotrope.OrdonnanceReutilisee when !string.IsNullOrWhiteSpace(d.NumeroOrdonnance) && d.MedecinId is not null
                                                                && h.UsagesOrdonnance >= r.Seuil =>
                    r.PeriodeJours is { } jours
                        ? $"Ordonnance n° {d.NumeroOrdonnance.Trim()} du même prescripteur déjà utilisée {h.UsagesOrdonnance} fois sur {jours} jours."
                        : $"Ordonnance n° {d.NumeroOrdonnance.Trim()} du même prescripteur déjà utilisée {h.UsagesOrdonnance} fois.",
                CodesAlertePsychotrope.TraitementEnCours when d.PatientId is not null && h.EnCours is { } t =>
                    $"Patient encore sous traitement psychotrope jusqu’au {t.Fin.AddDays(-1):dd/MM/yyyy} (délivrance du {t.Delivree:dd/MM/yyyy}, {t.DureeJours} jours).",
                CodesAlertePsychotrope.QuantiteAnormale => Quantite(r, d, produits, h),
                CodesAlertePsychotrope.PrescripteurInhabituel when d.MedecinId is not null && h.DelivrancesPrescripteur < r.Seuil =>
                    h.DelivrancesPrescripteur == 0
                        ? $"Prescripteur inhabituel : aucune délivrance sous registre sur {r.PeriodeJours} jours."
                        : $"Prescripteur inhabituel : {h.DelivrancesPrescripteur} délivrance(s) sous registre sur {r.PeriodeJours} jours (seuil {r.Seuil}).",
                CodesAlertePsychotrope.PrescripteurHorsZone when HorsZone(d, h) =>
                    $"Prescripteur hors zone : wilaya {h.WilayaMedecin} du médecin hors de la zone de l’officine.",
                _ => null
            };
            if (message is not null) alertes.Add(new AlertePsychotrope(r.Code, r.Libelle, message, r.ExigerValideur));
        }
        return alertes;
    }

    /// <summary>Hors zone : médecin choisi, sa wilaya connue, zone de l'officine réglée et ne la contenant pas.</summary>
    public static bool HorsZone(DelivrancePsychotrope d, HistoriquePsychotrope h) =>
        d.MedecinId is not null && !string.IsNullOrWhiteSpace(h.WilayaMedecin) && h.Zone is { Count: > 0 } zone && !zone.Contains(h.WilayaMedecin);

    /// <summary>Avis non bloquants : médecin sans wilaya (« wilaya à renseigner » ; aucune alerte hors zone dans ce cas).</summary>
    public static IReadOnlyList<string> Avis(DelivrancePsychotrope d, HistoriquePsychotrope h) =>
        d.MedecinId is not null && string.IsNullOrWhiteSpace(h.WilayaMedecin)
            ? ["Wilaya du prescripteur à renseigner (fiche du médecin) : le contrôle « hors zone » n’est pas possible."]
            : [];

    private static string? Quantite(RegleAlertePsychotrope r, DelivrancePsychotrope d, IReadOnlyDictionary<long, string> produits,
        HistoriquePsychotrope h)
    {
        var trop = d.Lignes.Where(l => l.Quantite > r.Seuil)
            .Select(l => $"{produits.GetValueOrDefault(l.ProduitId, "produit")} : {l.Quantite} boîtes").ToList();
        var parties = new List<string>();
        if (trop.Count > 0) parties.Add($"plus de {r.Seuil} boîtes par délivrance ({string.Join(", ", trop)})");
        if (d.DureeTraitementJours is { } j && j > r.PeriodeJours) parties.Add($"durée de traitement de {j} jours (au-delà de {r.PeriodeJours})");
        // Boîtes × contenance comparées à la durée (durée, contenance et unités par jour connues) ; durée absente : pas de contrôle
        if (d.DureeTraitementJours is { } duree and > 0 && r.UnitesParJour is { } parJour && h.Contenances is { } contenances)
        {
            var exces = d.Lignes.Where(l => contenances.TryGetValue(l.ProduitId, out var c) && (long)l.Quantite * c > (long)duree * parJour)
                .Select(l => $"{produits.GetValueOrDefault(l.ProduitId, "produit")} : {l.Quantite} × {contenances[l.ProduitId]} unités")
                .ToList();
            if (exces.Count > 0)
                parties.Add($"plus de {parJour} unités par jour pour {duree} jours de traitement ({string.Join(", ", exces)})");
        }
        return parties.Count == 0 ? null : "Quantité anormale : " + string.Join(" ; ", parties) + ".";
    }

    /// <summary>Alertes encore à forcer : celles dont un forçage récent ne couvre pas le code.</summary>
    public static IReadOnlyList<AlertePsychotrope> NonForcees(IReadOnlyList<AlertePsychotrope> alertes, IReadOnlySet<string> codesForces) =>
        alertes.Where(a => !codesForces.Contains(a.Code)).ToList();
}

/// <summary>Forçage enregistré (forcage_alerte_psychotrope, ajout seul).</summary>
public sealed record NouveauForcage(long UtilisateurId, long? ValidePar, IReadOnlyList<string> Codes, string Messages, string Motif,
    long? PatientId, long? MedecinId, string? NumeroOrdonnance, IReadOnlyList<long> Produits, string? Poste);

public sealed record ForcageAlerte(long Id, DateTime Date, string Employe, string? Valideur, IReadOnlyList<string> Codes, string Messages,
    string Motif, string? Patient, string? Medecin, string? NumeroOrdonnance, string Produits);

public sealed record LigneRegistreAnnulable(long RegistreId, long Numero, string Produit, string Type, DateTime Date, bool DejaAnnulee);

public sealed record AnnulationEcriture(long Numero, DateTime Date, string Motif, string Employe);

public sealed record ResultatConcordance(long Lignes, string? Motif, long? RegistreId, string? Attendu, string? Trouve);

public sealed record ClasseSuivie(string Code, string Libelle, bool Active, int NombreDci);
public sealed record DciClasse(long DciId, string Nom);
public sealed record LigneSuiviClasse(string Dci, int Annee, int Mois, int Boites, int Delivrances);

/// <summary>Accès aux tables du script 117.</summary>
public interface IRegistreRenforceDepot
{
    Task<IReadOnlyList<RegleAlertePsychotrope>> ReglesAsync(CancellationToken ct = default);
    Task EnregistrerReglesAsync(IReadOnlyList<RegleAlertePsychotrope> regles, long auteurId, CancellationToken ct = default);
    /// <summary>Produits de la liste qui sont sous registre (psychotrope ou stupéfiant), avec leur nom.</summary>
    Task<IReadOnlyDictionary<long, string>> ProduitsSousRegistreAsync(IReadOnlyCollection<long> produitIds, CancellationToken ct = default);
    Task<HistoriquePsychotrope> HistoriqueAsync(DelivrancePsychotrope d, IReadOnlyList<RegleAlertePsychotrope> regles, CancellationToken ct = default);
    Task<long> EnregistrerForcageAsync(NouveauForcage f, CancellationToken ct = default);
    /// <summary>Codes forcés par cet employé pour ce patient, ce prescripteur, ce n° et ces produits depuis <paramref name="depuisUtc"/>.</summary>
    Task<IReadOnlySet<string>> CodesForcesAsync(long utilisateurId, DelivrancePsychotrope d, DateTime depuisUtc, CancellationToken ct = default);
    Task<Page<ForcageAlerte>> ForcagesAsync(DateOnly du, DateOnly au, string? code, DemandePage page, CancellationToken ct = default);
    Task<LigneRegistreAnnulable?> LigneAsync(long numero, CancellationToken ct = default);
    Task<long> AnnulerAsync(long registreId, string motif, long utilisateurId, CancellationToken ct = default);
    /// <summary>Annulations des lignes de ces numéros d'ordre.</summary>
    Task<IReadOnlyDictionary<long, AnnulationEcriture>> AnnulationsAsync(IReadOnlyCollection<long> numeros, CancellationToken ct = default);
    Task<ResultatConcordance> ConcordanceAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ClasseSuivie>> ClassesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<DciClasse>> DciClasseAsync(string classe, CancellationToken ct = default);
    Task<IReadOnlyList<DciClasse>> ChercherDciAsync(string texte, CancellationToken ct = default);
    Task AjouterDciAsync(string classe, long dciId, long auteurId, CancellationToken ct = default);
    Task RetirerDciAsync(string classe, long dciId, CancellationToken ct = default);
    Task<IReadOnlyList<LigneSuiviClasse>> SuiviClasseAsync(string classe, DateOnly du, DateOnly au, CancellationToken ct = default);
}

// ---- Répertoire national des ordonnances (loi 23-05) [À VÉRIFIER : API] ----

public sealed record DelivranceRepertoire(string? NumeroOrdonnance, long? MedecinId, DateOnly? DatePrescription, IReadOnlyList<LignePsychotrope> Lignes);

/// <summary>Statut : « indisponible », « inconnue », « connue », « deja_delivree ».</summary>
public sealed record ReponseRepertoire(string Statut, string Message);

/// <summary>
/// Port vers le répertoire national des ordonnances (loi 23-05 ; API non publiée [À VÉRIFIER]). Adaptateur actuel : FACTICE.
/// Appelé à la délivrance d'un produit sous registre ; jamais bloquant (délai court, erreur ignorée).
/// Aucune donnée nominative du patient n'est transmise par l'adaptateur factice.
/// </summary>
public interface IRepertoireOrdonnancesConnecteur
{
    string Nom { get; }
    Task<ReponseRepertoire> SignalerDelivranceAsync(DelivranceRepertoire d, CancellationToken ct = default);
}

// ---- Cas d'usage ----

/// <summary>Avant l'encaissement d'un produit sous registre : alertes (avertissement) et répertoire national (jamais bloquant).</summary>
public sealed class ControlerDelivrancePsychotrope(IRegistreRenforceDepot depot, IPermissionService droits,
    IRepertoireOrdonnancesConnecteur? repertoire = null, ITraceErreurs? trace = null)
{
    public static readonly TimeSpan DelaiRepertoire = TimeSpan.FromSeconds(3);

    public async Task<ControleDelivrance> ExecuterAsync(long utilisateurId, DelivrancePsychotrope demande, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesDelivrer, "délivrer un psychotrope", ct);
        var (d, produits) = await SousRegistreAsync(depot, demande, ct);
        if (d.Lignes.Count == 0) return new ControleDelivrance([], null);
        var regles = await depot.ReglesAsync(ct);
        if (!regles.Any(r => r.Active)) return new ControleDelivrance([], await RepertoireAsync(d, ct));
        var historique = await depot.HistoriqueAsync(d, regles, ct);
        var alertes = EvaluationAlertesPsychotropes.Evaluer(regles, d, historique, produits);
        return new ControleDelivrance(alertes, await RepertoireAsync(d, ct), EvaluationAlertesPsychotropes.Avis(d, historique));
    }

    private async Task<string?> RepertoireAsync(DelivrancePsychotrope d, CancellationToken ct)
    {
        if (repertoire is null) return null;
        try
        {
            using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
            delai.CancelAfter(DelaiRepertoire);
            var r = await repertoire.SignalerDelivranceAsync(new DelivranceRepertoire(d.NumeroOrdonnance, d.MedecinId, d.DatePrescription, d.Lignes), delai.Token);
            return r.Message;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            trace?.Erreur("Répertoire national des ordonnances injoignable", e);
            return "Répertoire national des ordonnances injoignable : la délivrance n’est pas bloquée.";
        }
    }

    /// <summary>Ne garde que les produits sous registre (quantités regroupées par produit).</summary>
    internal static async Task<(DelivrancePsychotrope, IReadOnlyDictionary<long, string>)> SousRegistreAsync(IRegistreRenforceDepot depot,
        DelivrancePsychotrope d, CancellationToken ct)
    {
        var ids = d.Lignes.Select(l => l.ProduitId).Distinct().ToList();
        var produits = ids.Count == 0 ? new Dictionary<long, string>() : await depot.ProduitsSousRegistreAsync(ids, ct);
        var lignes = d.Lignes.Where(l => produits.ContainsKey(l.ProduitId)).GroupBy(l => l.ProduitId)
            .Select(g => new LignePsychotrope(g.Key, g.Sum(l => l.Quantite))).OrderBy(l => l.ProduitId).ToList();
        return (d with { Lignes = lignes, NumeroOrdonnance = string.IsNullOrWhiteSpace(d.NumeroOrdonnance) ? null : d.NumeroOrdonnance.Trim() }, produits);
    }

    internal static async Task<IReadOnlyList<AlertePsychotrope>> AlertesAsync(IRegistreRenforceDepot depot, DelivrancePsychotrope d,
        IReadOnlyDictionary<long, string> produits, CancellationToken ct)
    {
        var regles = await depot.ReglesAsync(ct);
        if (!regles.Any(r => r.Active)) return [];
        return EvaluationAlertesPsychotropes.Evaluer(regles, d, await depot.HistoriqueAsync(d, regles, ct), produits);
    }
}

/// <summary>
/// « Forcer » les alertes d'une délivrance : motif obligatoire, employé gardé ; une règle qui exige un valideur demande le droit
/// psychotropes.forcer_alerte (sinon « Faire autoriser » : code comptoir d'un ayant droit, gardé comme valideur). Journalisé.
/// </summary>
public sealed class ForcerAlertesPsychotropes(IRegistreRenforceDepot depot, IPermissionService droits, IJournal? journal = null,
    AutorisationCourante? autorisation = null)
{
    public const string PermissionForcer = Permissions.PsychotropesForcerAlerte;

    public async Task<long?> ExecuterAsync(long utilisateurId, DelivrancePsychotrope demande, string? motif, string? poste, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesDelivrer, "délivrer un psychotrope", ct);
        var texte = motif?.Trim() ?? "";
        if (texte.Length < 5) throw new RegleMetierException("Indiquez le motif du forçage (5 caractères au moins).", "motif");
        if (texte.Length > 500) throw new RegleMetierException("Motif trop long (500 caractères au plus).", "motif");
        var (d, produits) = await ControlerDelivrancePsychotrope.SousRegistreAsync(depot, demande, ct);
        var alertes = d.Lignes.Count == 0 ? [] : await ControlerDelivrancePsychotrope.AlertesAsync(depot, d, produits, ct);
        if (alertes.Count == 0) return null;   // plus rien à forcer

        long? valideur = null;
        if (alertes.Any(a => a.ExigeValideur))
        {
            if (!await droits.APermissionAsync(utilisateurId, PermissionForcer, ct))
                throw new DroitRefuseException(PermissionForcer, "forcer une alerte du registre des psychotropes",
                    "Alerte du registre des psychotropes : un pharmacien doit valider le forçage (code comptoir).");
            valideur = autorisation?.AutoriseParId ?? utilisateurId;
        }
        var id = await depot.EnregistrerForcageAsync(new NouveauForcage(utilisateurId, valideur, alertes.Select(a => a.Code).ToList(),
            string.Join(" | ", alertes.Select(a => a.Message)), texte, d.PatientId, d.MedecinId, d.NumeroOrdonnance,
            d.Lignes.Select(l => l.ProduitId).ToList(), poste), ct);
        if (journal is not null)
            await journal.EcrireAsync(utilisateurId, "psychotrope_alerte_forcee", "forcage_alerte_psychotrope", id,
                new Dictionary<string, object?> { ["codes"] = alertes.Select(a => a.Code).ToList(), ["valide_par"] = valideur },
                valideur is null ? Permissions.PsychotropesDelivrer : PermissionForcer, ct);
        return id;
    }
}

/// <summary>
/// Garde de l'encaissement : une délivrance sous registre avec une alerte non forcée (par le même employé, pour le même patient,
/// prescripteur, n° d'ordonnance et produits, depuis moins de <see cref="Validite"/>) est refusée.
/// </summary>
public sealed class ControleAlertesPsychotropes(IRegistreRenforceDepot depot, IHorloge horloge)
{
    public static readonly TimeSpan Validite = TimeSpan.FromMinutes(30);
    public const string Champ = "alerte_psychotrope";

    public async Task ExigerAsync(long utilisateurId, DelivrancePsychotrope demande, CancellationToken ct = default)
    {
        var (d, produits) = await ControlerDelivrancePsychotrope.SousRegistreAsync(depot, demande, ct);
        if (d.Lignes.Count == 0) return;
        var alertes = await ControlerDelivrancePsychotrope.AlertesAsync(depot, d, produits, ct);
        if (alertes.Count == 0) return;
        var forces = await depot.CodesForcesAsync(utilisateurId, d, horloge.MaintenantUtc - Validite, ct);
        var restantes = EvaluationAlertesPsychotropes.NonForcees(alertes, forces);
        if (restantes.Count > 0)
            throw new RegleMetierException("Alerte du registre des psychotropes : " + string.Join(" ", restantes.Select(a => a.Message))
                + " Forcez la délivrance avec un motif, ou abandonnez-la.", Champ);
    }
}

/// <summary>Réglage des alertes (titulaire).</summary>
public sealed class ParametrerAlertesPsychotropes(IRegistreRenforceDepot depot, IPermissionService droits, IJournal? journal = null,
    Reglages.IGardeReglagesCentraux? garde = null)
{
    public async Task<IReadOnlyList<RegleAlertePsychotrope>> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les alertes du registre des psychotropes", ct);
        return await depot.ReglesAsync(ct);
    }

    public async Task EnregistrerAsync(long utilisateurId, IReadOnlyList<RegleAlertePsychotrope> regles, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les alertes du registre des psychotropes", ct);
        foreach (var r in regles)
        {
            Pharma.Domain.Reglages.RegleAlertesPsychotropes.VerifierRegle(r.Code, r.Libelle, r.Seuil, r.PeriodeJours, r.UnitesParJour);
        }
        if (garde is not null) await garde.ExigerModifiableAsync(Pharma.Domain.Reglages.TablesMutualisables.AlertesPsychotropes, ct);
        var avant = journal is null ? null : await depot.ReglesAsync(ct);
        await depot.EnregistrerReglesAsync(regles, utilisateurId, ct);
        if (journal is not null)
        {
            var details = regles.ToDictionary(r => r.Code, r => (object?)new { r.Active, r.Seuil, r.PeriodeJours, r.ExigerValideur });            // Valeurs avant et après (D13)
            details["avant"] = avant!.ToDictionary(r => r.Code, r => (object?)new { r.Active, r.Seuil, r.PeriodeJours, r.ExigerValideur, r.UnitesParJour });
            details["apres"] = regles.ToDictionary(r => r.Code, r => (object?)new { r.Active, r.Seuil, r.PeriodeJours, r.ExigerValideur, r.UnitesParJour });
            await journal.EcrireAsync(utilisateurId, "reglages_alertes_psychotropes", "regle_alerte_psychotrope", null, details, null, ct);
        }
    }
}

/// <summary>Alertes forcées (titulaire, ou droit registre) : liste paginée et filtrée.</summary>
public sealed class ListerForcagesPsychotropes(IRegistreRenforceDepot depot, IPermissionService droits)
{
    public async Task<Page<ForcageAlerte>> ExecuterAsync(long utilisateurId, DateOnly du, DateOnly au, string? code, DemandePage page,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.PsychotropesRegistre, "consulter le registre des psychotropes", ct);
        if (au < du) throw new RegleMetierException("La date de début doit précéder la date de fin.");
        var p = await depot.ForcagesAsync(du, au, string.IsNullOrWhiteSpace(code) ? null : code, page, ct);
        if (await droits.EstTitulaireAsync(utilisateurId, ct)) return p;
        return p.Vers(f => f with { Patient = Stock.ConsulterRegistre.Initiales(f.Patient) });
    }
}

/// <summary>
/// Correction par écriture d'annulation (titulaire) : la ligne n'est jamais modifiée ; l'annulation (motif) est ajoutée et recopiée
/// dans le journal chaîné. Le stock se corrige par les opérations existantes (retour, écart d'inventaire), qui s'inscrivent au registre.
/// </summary>
public sealed class AnnulerEcritureRegistre(IRegistreRenforceDepot depot, IPermissionService droits, IJournal? journal = null)
{
    public async Task ExecuterAsync(long utilisateurId, long numero, string? motif, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "annuler une écriture du registre des psychotropes", ct);
        var texte = motif?.Trim() ?? "";
        if (texte.Length is < 5 or > 500) throw new RegleMetierException("Motif de l’annulation : 5 à 500 caractères.", "motif");
        var ligne = await depot.LigneAsync(numero, ct) ?? throw new RegleMetierException($"Ligne n° {numero} introuvable au registre.");
        if (ligne.DejaAnnulee) throw new RegleMetierException($"La ligne n° {numero} est déjà annulée.");
        var id = await depot.AnnulerAsync(ligne.RegistreId, texte, utilisateurId, ct);
        if (journal is not null)
            await journal.EcrireAsync(utilisateurId, "registre_ecriture_annulee", "annulation_registre", id,
                new Dictionary<string, object?> { ["numero"] = numero }, null, ct);
    }
}

/// <summary>Contrôle complémentaire de la vérification nocturne : le registre concorde avec son journal chaîné.</summary>
public sealed class ConcordanceRegistre(IRegistreRenforceDepot depot) : IControleJournal
{
    public const string Journal = "journal_registre_psychotrope";
    public string JournalControle => Journal;

    public async Task<(string? Motif, long? Rang, string? Attendu, string? Trouve)> ControlerAsync(CancellationToken ct = default)
    {
        var r = await depot.ConcordanceAsync(ct);
        return r.Motif is null ? (null, null, null, null)
            : ($"registre / journal : {r.Motif}{(r.RegistreId is { } id ? $" (ligne id {id})" : "")}", null, r.Attendu, r.Trouve);
    }
}

/// <summary>
/// Scellement mensuel du registre : le mois écoulé est scellé (ScellerJournal, motif cloture_mensuelle) dès le premier passage du
/// mois suivant, s'il ne l'est pas déjà. Le sceau couvre la dernière ligne du journal chaîné au moment du scellement.
/// </summary>
public sealed class ScellerRegistreMensuel(IIntegriteDepot integrite, ScellerJournal sceller)
{
    public async Task<SceauEnregistre?> ExecuterAsync(DateOnly aujourdhui, CancellationToken ct = default)
    {
        if (!(await integrite.JournauxAsync(ct)).Any(j => j.Journal == ConcordanceRegistre.Journal)) return null;
        var du = new DateOnly(aujourdhui.Year, aujourdhui.Month, 1).AddMonths(-1);
        var au = du.AddMonths(1).AddDays(-1);
        var sceaux = await integrite.SceauxAsync(ConcordanceRegistre.Journal, ct);
        if (sceaux.Any(s => s.Motif == MotifSceau.ClotureMensuelle && s.PeriodeDu == du)) return null;
        return await sceller.ExecuterAsync(ConcordanceRegistre.Journal, du, au, MotifSceau.ClotureMensuelle, null, ct: ct);
    }

    /// <summary>Texte du sceau imprimé sur un export d'inspection : sceaux mensuels de la période et état actuel de la chaîne.</summary>
    public static async Task<string?> TexteSceauAsync(IIntegriteDepot integrite, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        if (!(await integrite.JournauxAsync(ct)).Any(j => j.Journal == ConcordanceRegistre.Journal)) return null;
        var format = (await integrite.ParametresAsync(ct)).FormatExportSceau;
        var sceaux = (await integrite.SceauxAsync(ConcordanceRegistre.Journal, ct))
            .Where(s => s.Motif == MotifSceau.ClotureMensuelle && s.PeriodeDu <= au && s.PeriodeAu >= du).ToList();
        var dernier = await integrite.DernierMaillonAsync(ConcordanceRegistre.Journal, ScellerJournal.CleOfficine, ct);
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var parties = sceaux.Select(s => $"sceau n° {s.Id} du mois {s.PeriodeDu:MM/yyyy} ({s.Algorithme}, ligne {s.DernierRang}, clé {s.EmpreinteCle}) : "
                                         + MessageSceau.Exporter(s.Sceau, format)).ToList();
        if (parties.Count == 0) parties.Add("période non encore scellée (sceau mensuel au début du mois suivant)");
        parties.Add($"journal chaîné à ce jour : ligne {dernier.Rang.ToString(fr)}, empreinte {Convert.ToHexStringLower(dernier.Hash)}");
        return "Intégrité : " + string.Join(" · ", parties);
    }
}

// ---- Wilayas et zone de l'officine (script 118) ----

/// <summary>Wilaya d'Algérie (référentiel « wilaya » : code sur deux chiffres, nom).</summary>
public sealed record Wilaya(string Code, string Nom);

/// <summary>Zone de l'officine : sa wilaya et des wilayas voisines au choix. Officine vide : pas de contrôle « hors zone ».</summary>
public sealed record ZoneOfficine(string? Officine, IReadOnlyList<string> Voisines)
{
    public bool EstReglee => Officine is not null || Voisines.Count > 0;
}

/// <summary>Accès au référentiel des wilayas et à la zone de l'officine.</summary>
public interface IZonePsychotropesDepot
{
    Task<IReadOnlyList<Wilaya>> WilayasAsync(CancellationToken ct = default);
    Task<ZoneOfficine> ZoneAsync(CancellationToken ct = default);
    /// <summary>Remplace la zone (wilaya de l'officine et voisines).</summary>
    Task EnregistrerZoneAsync(ZoneOfficine zone, long auteurId, CancellationToken ct = default);
}

/// <summary>Code de wilaya : deux chiffres, de 01 à <see cref="Max"/> (référentiel en données, scripts 118 et 125).</summary>
public static class RegleWilaya
{
    public const int Max = 69;

    /// <summary>Met en forme (« 9 » → « 09 ») ; vide → null ; hors bornes → refus.</summary>
    public static string? Normaliser(string? code, string champ = "wilaya")
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var t = code.Trim();
        if (!int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || t.Length > 2 || n < 1 || n > Max)
            throw new RegleMetierException($"Wilaya : code de 01 à {Max}.", champ);
        return n.ToString("00", CultureInfo.InvariantCulture);
    }
}

/// <summary>Réglage de la zone de l'officine (titulaire) : wilaya de l'officine et wilayas voisines ; journalisé.</summary>
public sealed class ParametrerZonePsychotropes(IZonePsychotropesDepot depot, IPermissionService droits, IJournal? journal = null)
{
    public async Task<ZoneOfficine> LireAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la zone de l’officine", ct);
        return await depot.ZoneAsync(ct);
    }

    public async Task EnregistrerAsync(long utilisateurId, ZoneOfficine saisie, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler la zone de l’officine", ct);
        var officine = RegleWilaya.Normaliser(saisie.Officine, "wilaya_officine");
        var voisines = (saisie.Voisines ?? []).Select(v => RegleWilaya.Normaliser(v, "wilayas_voisines")).OfType<string>()
            .Where(v => v != officine).Distinct().OrderBy(v => v, StringComparer.Ordinal).ToList();
        if (officine is null && voisines.Count > 0)
            throw new RegleMetierException("Choisissez d’abord la wilaya de l’officine.", "wilaya_officine");
        var zone = new ZoneOfficine(officine, voisines);
        await depot.EnregistrerZoneAsync(zone, utilisateurId, ct);
        if (journal is not null)
            await journal.EcrireAsync(utilisateurId, "zone_psychotropes_reglee", "zone_psychotrope_wilaya", null,
                new Dictionary<string, object?> { ["officine"] = officine, ["voisines"] = voisines }, null, ct);
    }
}

/// <summary>Classes suivies (antibiotiques) : liste des DCI réglable par le titulaire, rapport des quantités délivrées.</summary>
public sealed class SuiviClassesTherapeutiques(IRegistreRenforceDepot depot, IPermissionService droits, IJournal? journal = null)
{
    public async Task<IReadOnlyList<ClasseSuivie>> ClassesAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.RapportsVentes, "consulter le suivi des antibiotiques", ct);
        return await depot.ClassesAsync(ct);
    }

    public async Task<IReadOnlyList<LigneSuiviClasse>> RapportAsync(long utilisateurId, string classe, DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.RapportsVentes, "consulter le suivi des antibiotiques", ct);
        if (au < du) throw new RegleMetierException("La date de début doit précéder la date de fin.");
        if (au.DayNumber - du.DayNumber > 3 * 366) throw new RegleMetierException("Période de trois ans au plus.");
        return await depot.SuiviClasseAsync(classe, du, au, ct);
    }

    public async Task<IReadOnlyList<DciClasse>> DciAsync(long utilisateurId, string classe, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.RapportsVentes, "consulter le suivi des antibiotiques", ct);
        return await depot.DciClasseAsync(classe, ct);
    }

    public async Task<IReadOnlyList<DciClasse>> ChercherDciAsync(long utilisateurId, string texte, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les classes suivies", ct);
        return string.IsNullOrWhiteSpace(texte) || texte.Trim().Length < 2 ? [] : await depot.ChercherDciAsync(texte.Trim(), ct);
    }

    public async Task ModifierAsync(long utilisateurId, string classe, long dciId, bool ajouter, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les classes suivies", ct);
        if (!(await depot.ClassesAsync(ct)).Any(c => c.Code == classe)) throw new RegleMetierException("Classe suivie inconnue.");
        if (ajouter) await depot.AjouterDciAsync(classe, dciId, utilisateurId, ct);
        else await depot.RetirerDciAsync(classe, dciId, ct);
        if (journal is not null)
            await journal.EcrireAsync(utilisateurId, "classe_suivie_modifiee", "classe_suivie_dci", dciId,
                new Dictionary<string, object?> { ["classe"] = classe, ["ajout"] = ajouter }, null, ct);
    }
}
