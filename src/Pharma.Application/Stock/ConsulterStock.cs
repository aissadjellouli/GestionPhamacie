using System.Globalization;
using Pharma.Application.Commun;
using Pharma.Domain.Catalogue;

namespace Pharma.Application.Stock;

/// <summary>ValeurAchat : valeur du stock au prix d'achat, null sans le droit de la voir (rapports.marges, titulaire).</summary>
public sealed record IndicateursStock(int ReferencesEnStock, decimal? ValeurAchat, int LotsDateAVerifier);

/// <summary>
/// Une ligne du tableau de stock. <see cref="Statut"/> vient de la vue v_etat_stock :
/// ok, rupture, critique, dormant ou surstock.
/// </summary>
public sealed record LigneStock(
    long ProduitId, string NomCommercial, string? Dci, string? CodeBarre, ClasseReglementaire Classe,
    int Stock, int? CouvertureJours, DateOnly? PremierePeremption, int NombreLots,
    IReadOnlyList<string> Grossistes, IReadOnlyList<string> NumerosLots,
    string Statut, bool PeremptionProche, long? EmplacementId = null, string? Emplacement = null, long? ReserveId = null, string? Reserve = null);

public sealed record EtatStock(IndicateursStock Indicateurs, IReadOnlyList<LigneStock> Lignes);

public interface IStockLecture
{
    Task<IndicateursStock> IndicateursAsync(CancellationToken ct = default);
    Task<IReadOnlyList<LigneStock>> LignesAsync(CancellationToken ct = default);
}

/// <summary>Filtres du tableau de stock : recherche (nom, DCI, code-barres, lot, emplacement), filtre (tous, commander, peremption, psychotropes), rayon (« - » = non rangé).</summary>
public sealed record FiltreStock(string? Recherche = null, string? Filtre = null, string? Rayon = null);

/// <summary>Page du tableau de stock, indicateurs de tout le stock, et propositions pour aider la saisie de la recherche.</summary>
public sealed record PageStock(IndicateursStock Indicateurs, Page<LigneStock> Page, IReadOnlyList<string> Suggestions);

/// <summary>Écran Stock : indicateurs et liste des produits actifs (complète, ou paginée et filtrée par le serveur).</summary>
public sealed class ConsulterStock(IStockLecture stock, IEmplacements? emplacements = null)
{
    private static readonly CompareInfo Fr = CultureInfo.GetCultureInfo("fr-FR").CompareInfo;
    private const CompareOptions SansCasseNiAccent = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    public async Task<EtatStock> ExecuterAsync(CancellationToken ct = default, bool voirValeurAchat = false) =>
        new(await IndicateursAsync(voirValeurAchat, ct), await stock.LignesAsync(ct));

    /// <summary>Valeur au prix d'achat retirée sans le droit de la voir (le serveur ne l'envoie pas).</summary>
    private async Task<IndicateursStock> IndicateursAsync(bool voirValeurAchat, CancellationToken ct)
    {
        var i = await stock.IndicateursAsync(ct);
        return voirValeurAchat ? i : i with { ValeurAchat = null };
    }

    /// <summary>Tableau filtré et paginé par le serveur ; la recherche ignore majuscules et accents.</summary>
    public async Task<PageStock> PageAsync(FiltreStock filtre, DemandePage page, CancellationToken ct = default, bool voirValeurAchat = false)
    {
        var lignes = await stock.LignesAsync(ct);
        var rayon = filtre.Rayon?.Trim() ?? "";
        var lieux = rayon is "" or "-" || emplacements is null ? [] : await emplacements.ListerAsync(ct);
        var texte = filtre.Recherche?.Trim() ?? "";
        var retenues = lignes.Where(l => PasseFiltre(l, filtre.Filtre) && PasseRecherche(l, texte) && PasseRayon(l, rayon, lieux)).ToList();
        return new PageStock(await IndicateursAsync(voirValeurAchat, ct),
            new Page<LigneStock>(retenues.Skip(page.Decalage).Take(page.Taille).ToList(), retenues.Count, page.Page, page.Taille),
            Suggestions(lignes, texte));
    }

    private static bool Contient(string? s, string t) => s is not null && Fr.IndexOf(s, t, SansCasseNiAccent) >= 0;

    private static bool PasseFiltre(LigneStock l, string? filtre) => filtre switch
    {
        "commander" => l.Statut is "rupture" or "critique",
        "peremption" => l.PeremptionProche,
        "psychotropes" => l.Classe is ClasseReglementaire.Psychotrope or ClasseReglementaire.Stupefiant,
        _ => true
    };

    private static bool PasseRecherche(LigneStock l, string t) =>
        t.Length == 0 || Contient(l.NomCommercial, t) || Contient(l.Dci, t) || l.CodeBarre == t
        || l.NumerosLots.Any(n => n.Equals(t, StringComparison.OrdinalIgnoreCase))
        || string.Equals(l.Emplacement, t, StringComparison.OrdinalIgnoreCase);

    private static bool PasseRayon(LigneStock l, string rayon, IReadOnlyList<Emplacement> lieux) => rayon switch
    {
        "" => true,
        "-" => l.EmplacementId is null,
        _ => lieux.FirstOrDefault(e => e.Id == l.EmplacementId)?.Rayon == rayon
    };

    /// <summary>
    /// Aide à la saisie : dès 2 caractères, jusqu'à 8 propositions prises dans le stock (noms, DCI, lots, emplacements), celles qui
    /// commencent par le texte d'abord.
    /// </summary>
    private static IReadOnlyList<string> Suggestions(IReadOnlyList<LigneStock> lignes, string t)
    {
        if (t.Length < 2) return [];
        var candidats = lignes.SelectMany(l => new[] { l.NomCommercial, l.Dci, l.Emplacement }.Concat(l.NumerosLots))
            .OfType<string>().Where(s => Contient(s, t)).Distinct(StringComparer.OrdinalIgnoreCase);
        return candidats.OrderBy(s => Fr.IsPrefix(s, t, SansCasseNiAccent) ? 0 : 1).ThenBy(s => s, StringComparer.Create(CultureInfo.GetCultureInfo("fr-FR"), true))
            .Take(8).ToList();
    }
}
