using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Suivi;

/// <summary>
/// Somme due, lue dans v_suivi_dettes (039). Sens : « creance » (due à la pharmacie) ou « dette » (due par elle).
/// Categorie : patient, facture_patient, facture_entreprise, facture_organisme, bordereau, grossiste, effet.
/// Depuis : date depuis laquelle la somme est due (date d'établissement d'une facture) ; Echeance : date d'échéance d'une facture client, ou date de paiement prévue d'un effet.
/// </summary>
public sealed record SommeDue(string Sens, string Categorie, long? TiersId, string Tiers, string Libelle, DateOnly Depuis, DateOnly? Echeance,
    decimal Montant, bool Rejetee);

/// <summary>Somme due avec son ancienneté. EnAlerte : facture échue ou rejetée, somme plus ancienne que le délai réglé, ou effet à payer sous peu.</summary>
public sealed record LigneSuivi(SommeDue Somme, int Jours, string Tranche, bool EnAlerte);

/// <summary>Alerte par catégorie de tiers : nombre de sommes concernées, total, message prêt à afficher.</summary>
public sealed record AlerteDette(string Sens, string Categorie, int Nombre, decimal Montant, string Message);

public sealed record TotalTranche(string Sens, string Tranche, int Nombre, decimal Montant);

public sealed record SuiviDettes(int DelaiJours, DateOnly Jour, IReadOnlyList<LigneSuivi> Lignes, IReadOnlyList<TotalTranche> Tranches,
    IReadOnlyList<AlerteDette> Alertes)
{
    public decimal TotalCreances => Lignes.Where(l => l.Somme.Sens == "creance").Sum(l => l.Somme.Montant);
    /// <summary>Factures des grossistes à payer ; les effets déjà remis n'y sont pas (ils ont déjà diminué la dette).</summary>
    public decimal TotalDettes => Lignes.Where(l => l.Somme is { Sens: "dette", Categorie: not "effet" }).Sum(l => l.Somme.Montant);
}

public interface ISuiviDettes
{
    Task<IReadOnlyList<SommeDue>> SommesDuesAsync(CancellationToken ct = default);
    Task<int> DelaiAlerteAsync(CancellationToken ct = default);
    Task ModifierDelaiAlerteAsync(int jours, CancellationToken ct = default);
}

/// <summary>Ancienneté, tranches (échéancier) et alertes, à partir des sommes dues : calcul sans base, testable.</summary>
public static class RegleSuiviDettes
{
    /// <summary>Une traite (code « effet ») est signalée quand son échéance arrive dans ce nombre de jours, ou est passée sans paiement.</summary>
    public const int JoursAvantEcheance = 7;
    public static readonly string[] Tranches = ["0-30", "31-60", "61+"];

    public static bool EstFacture(string categorie) => categorie.StartsWith("facture_", StringComparison.Ordinal);

    public static string Tranche(int jours) => jours <= 30 ? Tranches[0] : jours <= 60 ? Tranches[1] : Tranches[2];

    public static SuiviDettes Construire(IEnumerable<SommeDue> sommes, int delaiJours, DateOnly aujourdhui)
    {
        var lignes = sommes.Where(s => s.Montant > 0).Select(s =>
        {
            var jours = Math.Max(aujourdhui.DayNumber - s.Depuis.DayNumber, 0);
            // Effet : à payer sous peu. Facture client : rejetée, ou échéance dépassée. Autres : plus anciennes que le délai réglé.
            var alerte = s.Categorie == "effet"
                ? s.Echeance is { } e && e.DayNumber - aujourdhui.DayNumber <= JoursAvantEcheance
                : s.Rejetee || (EstFacture(s.Categorie) && s.Echeance is { } due ? due < aujourdhui : jours > delaiJours);
            return new LigneSuivi(s, jours, Tranche(jours), alerte);
        }).OrderByDescending(l => l.EnAlerte).ThenByDescending(l => l.Jours).ThenBy(l => l.Somme.Tiers, StringComparer.CurrentCulture).ToList();

        var tranches = lignes.Where(l => l.Somme.Categorie != "effet")
            .GroupBy(l => (l.Somme.Sens, l.Tranche))
            .Select(g => new TotalTranche(g.Key.Sens, g.Key.Tranche, g.Count(), g.Sum(l => l.Somme.Montant)))
            .OrderBy(t => t.Sens).ThenBy(t => Array.IndexOf(Tranches, t.Tranche)).ToList();

        var alertes = lignes.Where(l => l.EnAlerte).GroupBy(l => (l.Somme.Sens, l.Somme.Categorie))
            .Select(g => new AlerteDette(g.Key.Sens, g.Key.Categorie, g.Count(), g.Sum(l => l.Somme.Montant),
                Message(g.Key.Categorie, g.Count(), g.Select(l => l.Somme.Tiers).Distinct().Count(), g.Sum(l => l.Somme.Montant), delaiJours,
                    g.Count(l => l.Somme.Rejetee))))
            .OrderBy(a => a.Sens).ThenByDescending(a => a.Montant).ToList();
        return new SuiviDettes(delaiJours, aujourdhui, lignes, tranches, alertes);
    }

    private static string Message(string categorie, int nombre, int tiers, decimal montant, int delai, int rejetees)
    {
        var da = montant.ToString("N2", System.Globalization.CultureInfo.GetCultureInfo("fr-FR")) + " DA";
        var rejet = rejetees > 0 ? $", dont {rejetees} rejetée(s)" : "";
        return categorie switch
        {
            "patient" => $"{tiers} patient(s) ont une dette de plus de {delai} jours : {da} à recouvrer.",
            "facture_patient" => $"{nombre} facture(s) de patients échue(s){rejet} : {da} à encaisser.",
            "facture_entreprise" => $"{nombre} facture(s) d'entreprises échue(s){rejet} : {da} à encaisser.",
            "facture_organisme" => $"{nombre} facture(s) d'organismes échue(s){rejet} : {da} à encaisser.",
            "bordereau" => $"{nombre} bordereau(x) d'organismes envoyés depuis plus de {delai} jours ou rejetés en partie : {da} à recevoir.",
            "grossiste" => $"{nombre} facture(s) de {tiers} grossiste(s) à payer depuis plus de {delai} jours : {da}.",
            "effet" => $"{nombre} traite(s) échue(s) ou à payer dans les {JoursAvantEcheance} jours : {da} à prévoir.",
            _ => $"{nombre} somme(s) due(s) : {da}."
        };
    }
}

/// <summary>
/// Filtres de la liste des sommes dues. Categorie : patient, facture (toutes les factures clients), bordereau, grossiste, effet, ou null (tout).
/// Tiers : identifiant du tiers, ou son nom quand il n'a pas d'identifiant. Etat : alerte, delai (dans le délai), rejetee, ou null (tous).
/// Du / Au : période sur la date depuis laquelle la somme est due. Recherche : partie du nom du tiers (sans majuscules ni accents),
/// pour les tiers sans fin (patients, clients) où une liste de tous les noms n'aurait pas de sens.
/// </summary>
public sealed record FiltreSuiviDettes(string? Categorie = null, string? Tiers = null, string? Etat = null, DateOnly? Du = null, DateOnly? Au = null,
    string? Recherche = null)
{
    public static readonly string[] Categories = ["patient", "facture", "bordereau", "grossiste", "effet"];
    public static readonly string[] Etats = ["alerte", "delai", "rejetee"];

    /// <summary>Valeurs inconnues ignorées (pas d'erreur : un lien ancien ou modifié affiche simplement tout).</summary>
    /// <summary>Catégories aux tiers peu nombreux : leur filtre est une liste complète ; les autres (patients, clients, organismes des bordereaux) une recherche.</summary>
    public static readonly string[] CategoriesListeTiers = ["grossiste", "effet"];

    public static FiltreSuiviDettes Lire(string? categorie, string? tiers, string? etat, DateOnly? du, DateOnly? au, string? recherche = null) => new(
        Categories.Contains(categorie) ? categorie : null,
        string.IsNullOrWhiteSpace(tiers) ? null : tiers.Trim(),
        Etats.Contains(etat) ? etat : null, du, au,
        string.IsNullOrWhiteSpace(recherche) ? null : recherche.Trim());
}

/// <summary>Tiers proposé dans le filtre : Valeur à renvoyer (identifiant, ou nom sans identifiant) et nom affiché.</summary>
public sealed record TiersSuivi(string Valeur, string Nom);

/// <summary>
/// Une page de sommes dues, le total en DA de tout le filtre (toutes pages), et les tiers proposés pour le filtre : tous ceux de la
/// catégorie quand ils sont peu nombreux (grossistes, effets), sinon ceux qui répondent à la recherche (10 au plus, dès 2 lettres).
/// </summary>
public sealed record PageSuiviDettes(Page<LigneSuivi> Page, decimal Total, IReadOnlyList<TiersSuivi> Tiers);

/// <summary>Liste des sommes dues filtrée et paginée par le serveur : calcul sans base, testable.</summary>
public static class FiltrageSuiviDettes
{
    public const int PropositionsMax = 10;

    /// <summary>Texte comparé sans majuscules ni accents.</summary>
    public static string Normaliser(string texte) => TexteRecherche.Normaliser(texte);

    public static string ValeurTiers(SommeDue s) => s.TiersId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "nom:" + s.Tiers;

    private static bool DansCategorie(LigneSuivi l, string? categorie) => categorie switch
    {
        null => true,
        "facture" => RegleSuiviDettes.EstFacture(l.Somme.Categorie),
        _ => l.Somme.Categorie == categorie
    };

    public static PageSuiviDettes Filtrer(SuiviDettes suivi, FiltreSuiviDettes f, DemandePage page)
    {
        var categorie = suivi.Lignes.Where(l => DansCategorie(l, f.Categorie)).ToList();
        // Tiers de la catégorie, quels que soient les autres filtres ; un même nom porté par deux tiers est distingué par son n°
        var tiers = categorie.GroupBy(l => ValeurTiers(l.Somme)).Select(g => (Valeur: g.Key, Nom: g.First().Somme.Tiers)).ToList();
        var homonymes = tiers.GroupBy(t => t.Nom).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var recherche = f.Recherche is { Length: > 0 } r ? Normaliser(r) : null;
        var tousProposes = FiltreSuiviDettes.CategoriesListeTiers.Contains(f.Categorie);
        var listeTiers = tiers
            .Where(t => tousProposes || (recherche is { Length: >= 2 } && Normaliser(t.Nom).Contains(recherche, StringComparison.Ordinal)))
            .Select(t => new TiersSuivi(t.Valeur, homonymes.Contains(t.Nom) && !t.Valeur.StartsWith("nom:", StringComparison.Ordinal) ? $"{t.Nom} (n° {t.Valeur})" : t.Nom))
            .OrderBy(t => t.Nom, StringComparer.CurrentCulture).Take(tousProposes ? int.MaxValue : PropositionsMax).ToList();

        // Même ordre que le suivi : alertes d'abord, puis les plus anciennes
        var lignes = categorie.Where(l =>
            (f.Tiers is null || ValeurTiers(l.Somme) == f.Tiers)
            && (f.Tiers is not null || recherche is null || Normaliser(l.Somme.Tiers).Contains(recherche, StringComparison.Ordinal))
            && (f.Etat is null || f.Etat switch { "alerte" => l.EnAlerte, "delai" => !l.EnAlerte, "rejetee" => l.Somme.Rejetee, _ => true })
            && (f.Du is null || l.Somme.Depuis >= f.Du)
            && (f.Au is null || l.Somme.Depuis <= f.Au)).ToList();
        var pageLignes = lignes.Skip(page.Decalage).Take(page.Taille).ToList();
        return new PageSuiviDettes(new Page<LigneSuivi>(pageLignes, lignes.Count, page.Page, page.Taille), lignes.Sum(l => l.Somme.Montant), listeTiers);
    }
}

/// <summary>Dettes et créances de tous les tiers, avec échéancier et alertes (droit « tresorerie.gerer »).</summary>
public sealed class ObtenirSuiviDettes(ISuiviDettes suivi, IPermissionService droits, IHorloge horloge)
{
    public async Task<SuiviDettes> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter les dettes et créances", ct);
        return await CalculerAsync(ct);
    }

    /// <summary>Liste des sommes dues filtrée et paginée par le serveur (même droit que le suivi).</summary>
    public async Task<PageSuiviDettes> LignesAsync(long utilisateurId, FiltreSuiviDettes filtre, DemandePage page, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.TresorerieGerer, "consulter les dettes et créances", ct);
        return FiltrageSuiviDettes.Filtrer(await CalculerAsync(ct), filtre, page);
    }

    /// <summary>Sans contrôle de droit : pour le tableau de bord du titulaire, qui a déjà vérifié le sien.</summary>
    public async Task<SuiviDettes> CalculerAsync(CancellationToken ct = default) =>
        RegleSuiviDettes.Construire(await suivi.SommesDuesAsync(ct), await suivi.DelaiAlerteAsync(ct), horloge.Aujourdhui);
}

/// <summary>Le titulaire règle l'ancienneté à partir de laquelle une dette ou une créance est signalée ; inscrit au journal.</summary>
public sealed class ModifierDelaiAlerteDettes(ISuiviDettes suivi, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(int jours, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler le délai d'alerte des dettes", ct);
        if (jours is < 1 or > 365) throw new RegleMetierException("Délai d'alerte entre 1 et 365 jours.", "delai_alerte");
        var avant = await suivi.DelaiAlerteAsync(ct);
        await suivi.ModifierDelaiAlerteAsync(jours, ct);
        await journal.EcrireAsync(utilisateurId, "delai_alerte_dettes", "parametre_caisse", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = jours }, Permissions.AdminParametres, ct);
    }
}
