using Pharma.Domain.Securite;

namespace Pharma.Application.Alertes;

public sealed record Alerte(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot, string? Emplacement = null);

public interface IAlertesLecture
{
    /// <summary>Alertes nouvelles ou vues des types donnés (AlertesSelonDroits), triées par urgence puis par montant en jeu.</summary>
    Task<IReadOnlyList<Alerte>> ListerOuvertesAsync(IReadOnlyCollection<string> types, CancellationToken ct = default);
    /// <summary>Nombre d'alertes urgentes ouvertes des types donnés (pastille du menu, tuile de l'accueil).</summary>
    Task<int> CompterUrgentesAsync(IReadOnlyCollection<string> types, CancellationToken ct = default);
}

/// <summary>Alertes réservées au titulaire : elles nomment un employé (ancien lot délaissé par un vendeur).</summary>
public static class AlertesReservees
{
    public const string LotDelaisse = "lot_delaisse";
    public static readonly IReadOnlySet<string> Types = new HashSet<string> { LotDelaisse };
}

/// <summary>
/// Une alerte n'est montrée (liste, compteur du menu, tuile de l'accueil) et ne peut être traitée ou reportée que par qui peut agir
/// dessus (choix du 2026-11-13, docs/regles-metier.md) : rupture et stock critique → commander ; surstock et dormant → commander
/// ou ajuster le stock ; périmé → déclarer périmé ou retourner au grossiste ; péremption proche et risque d'écoulement → vendre
/// d'abord ou retourner ; ancien lot délaissé → titulaire seul. Le titulaire voit tout.
/// </summary>
public static class AlertesSelonDroits
{
    private static readonly (string Type, string[] Droits)[] Regles =
    [
        ("rupture", [Permissions.AchatsCommander]),
        ("stock_critique", [Permissions.AchatsCommander]),
        ("surstock", [Permissions.AchatsCommander, Permissions.StockAjuster]),
        ("dormant", [Permissions.AchatsCommander, Permissions.StockAjuster]),
        ("perime", [Permissions.StockDeclarerPerime, Permissions.AchatsRetourFournisseur]),
        ("peremption", [Permissions.CaisseVendre, Permissions.AchatsRetourFournisseur]),
        ("risque_ecoulement", [Permissions.CaisseVendre, Permissions.AchatsRetourFournisseur]),
        (AlertesReservees.LotDelaisse, []),   // titulaire seul
    ];

    /// <summary>Tous les types d'alerte connus (contrainte alerte_type_check).</summary>
    public static IReadOnlyList<string> Tous => Regles.Select(r => r.Type).ToList();

    public static IReadOnlyList<string> TypesVisibles(bool titulaire, IReadOnlyCollection<string> permissions) =>
        titulaire ? Tous : Regles.Where(r => r.Droits.Any(permissions.Contains)).Select(r => r.Type).ToList();

    public static async Task<IReadOnlyList<string>> TypesVisiblesAsync(Securite.IPermissionService droits, long utilisateurId, CancellationToken ct) =>
        TypesVisibles(await droits.EstTitulaireAsync(utilisateurId, ct), (await droits.ListerAsync(utilisateurId, ct)).ToHashSet());
}

/// <summary>Ancien lot délaissé, créé ou passé à « urgent » après une vente (fonction SQL alerter_lots_delaisses).</summary>
public sealed record AlerteLotDelaisse(long Id, string Niveau, string Message);

public interface IAlertesLotsDelaisses
{
    /// <summary>Calcule l'alerte pour les produits vendus ; renvoie les alertes à notifier au titulaire.</summary>
    Task<IReadOnlyList<AlerteLotDelaisse>> AlerterAsync(IReadOnlyCollection<long> produits, CancellationToken ct = default);
}

/// <summary>Calcul des alertes (aujourd'hui assuré par la fonction SQL generer_alertes).</summary>
public interface IMoteurAlertes
{
    Task<int> GenererAsync(CancellationToken ct = default);
}

public sealed class ListerAlertesOuvertes(IAlertesLecture alertes, Securite.IPermissionService droits)
{
    /// <summary>
    /// Alertes que l'employé peut traiter. Sans le droit de voir les prix d'achat (rapports.marges, titulaire), la valeur au prix
    /// d'achat n'est pas envoyée : ni ValeurDzd, ni le montant cité dans le message.
    /// </summary>
    public async Task<IReadOnlyList<Alerte>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        var liste = await alertes.ListerOuvertesAsync(await AlertesSelonDroits.TypesVisiblesAsync(droits, utilisateurId, ct), ct);
        return await Securite.PermissionServiceExtensions.VoitPrixAchatAsync(droits, utilisateurId, ct) ? liste : liste.Select(SansValeur).ToList();
    }

    /// <summary>Alerte sans sa valeur au prix d'achat (« (1 234,00 DA au prix d'achat) » retiré du message).</summary>
    public static Alerte SansValeur(Alerte a) => a with
    {
        ValeurDzd = null,
        Message = System.Text.RegularExpressions.Regex.Replace(a.Message, @"\s*\([^()]*DA au prix d['’]achat\)", "")
    };

    public async Task<int> CompterUrgentesAsync(long utilisateurId, CancellationToken ct = default) =>
        await alertes.CompterUrgentesAsync(await AlertesSelonDroits.TypesVisiblesAsync(droits, utilisateurId, ct), ct);

    /// <summary>
    /// Liste des alertes paginée et filtrée par le serveur (toutes, urgent, peremption, stock), avec ce que les tuiles et les
    /// onglets affichent, calculé sur toutes les alertes ouvertes (pas sur la seule page).
    /// </summary>
    public async Task<PageAlertes> PageAsync(long utilisateurId, string? filtre, Commun.DemandePage page, CancellationToken ct = default)
    {
        var toutes = await ExecuterAsync(utilisateurId, ct);
        var retenues = toutes.Where(a => Correspond(a, filtre)).ToList();
        return new PageAlertes(
            new Commun.Page<Alerte>(retenues.Skip(page.Decalage).Take(page.Taille).ToList(), retenues.Count, page.Page, page.Taille),
            Filtres.ToDictionary(f => f, f => toutes.Count(a => Correspond(a, f))),
            toutes.GroupBy(a => a.Type).ToDictionary(g => g.Key, g => g.Count()),
            await Securite.PermissionServiceExtensions.VoitPrixAchatAsync(droits, utilisateurId, ct) ? toutes.Where(EstPeremption).Sum(a => a.ValeurDzd ?? 0) : null);
    }

    public static readonly IReadOnlyList<string> Filtres = ["toutes", "urgent", "peremption", "stock"];

    public static bool EstPeremption(Alerte a) => a.Type is "peremption" or "perime" or "risque_ecoulement";

    private static bool Correspond(Alerte a, string? filtre) => filtre switch
    {
        "urgent" => a.Niveau == "urgent",
        "peremption" => EstPeremption(a),
        "stock" => !EstPeremption(a),
        _ => true
    };
}

/// <summary>Page d'alertes ; nombre par onglet et par type, et valeur à risque (péremption, prix d'achat : null sans le droit), sur toutes les alertes ouvertes.</summary>
public sealed record PageAlertes(Commun.Page<Alerte> Page, IReadOnlyDictionary<string, int> ParFiltre, IReadOnlyDictionary<string, int> ParType,
    decimal? ValeurARisque);

/// <summary>Crée, met à jour ou clôt les alertes. Renvoie le nombre d'alertes créées ou mises à jour.</summary>
public sealed class GenererAlertes(IMoteurAlertes moteur)
{
    public Task<int> ExecuterAsync(CancellationToken ct = default) => moteur.GenererAsync(ct);
}
