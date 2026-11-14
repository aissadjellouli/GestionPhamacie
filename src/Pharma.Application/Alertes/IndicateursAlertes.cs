using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Alertes;

/// <summary>Lots vendables qui périment dans les N jours (cumulé : « 60 jours » compte aussi ceux de 30 jours) ; Valeur au prix d'achat, null sans le droit de la voir.</summary>
public sealed record HorizonPeremption(int Jours, int Lots, int Boites, decimal? Valeur);

/// <summary>
/// Indicateurs de l'écran Alertes, en plus des alertes ouvertes. ReglementsAPreparer : sommes dues aux grossistes en alerte
/// (factures plus anciennes que le délai réglé, effets à payer sous peu) ; null sans le droit de régler les grossistes.
/// </summary>
public sealed record IndicateursAlertes(IReadOnlyList<HorizonPeremption> Peremption, int? ReglementsAPreparer, decimal? MontantAPreparer);

public interface IIndicateursAlertes
{
    /// <summary>Pour chaque horizon : lots non bloqués, en stock, qui périment après aujourd'hui et au plus tard dans N jours.</summary>
    Task<IReadOnlyList<HorizonPeremption>> HorizonsAsync(DateOnly aujourdhui, IReadOnlyList<int> jours, CancellationToken ct = default);
}

public sealed class ObtenirIndicateursAlertes(IIndicateursAlertes indicateurs, IPermissionService droits, IHorloge horloge, Suivi.ObtenirSuiviDettes suivi)
{
    public static readonly int[] Horizons = [30, 60, 180];

    public async Task<IndicateursAlertes> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockConsulter, "consulter les alertes", ct);
        var horizons = await indicateurs.HorizonsAsync(horloge.Aujourdhui, Horizons, ct);
        // Valeur au prix d'achat : droit « rapports.marges » ou titulaire
        if (!await droits.VoitPrixAchatAsync(utilisateurId, ct)) horizons = horizons.Select(h => h with { Valeur = null }).ToList();
        if (!await droits.APermissionAsync(utilisateurId, Permissions.AchatsRegler, ct)) return new IndicateursAlertes(horizons, null, null);

        // Même calcul que l'écran Dettes et créances : rien à recompter ici
        var dettes = (await suivi.CalculerAsync(ct)).Lignes
            .Where(l => l.EnAlerte && l.Somme.Sens == "dette" && l.Somme.Categorie is "grossiste" or "effet").ToList();
        return new IndicateursAlertes(horizons, dettes.Count, dettes.Sum(l => l.Somme.Montant));
    }
}
