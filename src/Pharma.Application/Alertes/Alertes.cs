namespace Pharma.Application.Alertes;

public sealed record Alerte(
    long Id, string Type, string Niveau, long ProduitId, long? LotId,
    string Message, decimal? ValeurDzd, string Statut, DateTime MiseAJourLe,
    string Produit, string? NumeroLot, string? Emplacement = null);

public interface IAlertesLecture
{
    /// <summary>Alertes nouvelles ou vues, triées par urgence puis par montant en jeu ; titulaire : aussi les alertes qui lui sont réservées.</summary>
    Task<IReadOnlyList<Alerte>> ListerOuvertesAsync(bool titulaire, CancellationToken ct = default);
    /// <summary>Nombre d'alertes urgentes ouvertes (pastille du bandeau, lue à chaque navigation).</summary>
    Task<int> CompterUrgentesAsync(bool titulaire, CancellationToken ct = default);
}

/// <summary>Alertes réservées au titulaire : elles nomment un employé (ancien lot délaissé par un vendeur).</summary>
public static class AlertesReservees
{
    public const string LotDelaisse = "lot_delaisse";
    public static readonly IReadOnlySet<string> Types = new HashSet<string> { LotDelaisse };
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
    public async Task<IReadOnlyList<Alerte>> ExecuterAsync(long utilisateurId, CancellationToken ct = default) =>
        await alertes.ListerOuvertesAsync(await droits.EstTitulaireAsync(utilisateurId, ct), ct);

    public async Task<int> CompterUrgentesAsync(long utilisateurId, CancellationToken ct = default) =>
        await alertes.CompterUrgentesAsync(await droits.EstTitulaireAsync(utilisateurId, ct), ct);

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
            toutes.Where(EstPeremption).Sum(a => a.ValeurDzd ?? 0));
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

/// <summary>Page d'alertes ; nombre par onglet et par type, et valeur à risque (péremption), sur toutes les alertes ouvertes.</summary>
public sealed record PageAlertes(Commun.Page<Alerte> Page, IReadOnlyDictionary<string, int> ParFiltre, IReadOnlyDictionary<string, int> ParType,
    decimal ValeurARisque);

/// <summary>Crée, met à jour ou clôt les alertes. Renvoie le nombre d'alertes créées ou mises à jour.</summary>
public sealed class GenererAlertes(IMoteurAlertes moteur)
{
    public Task<int> ExecuterAsync(CancellationToken ct = default) => moteur.GenererAsync(ct);
}
