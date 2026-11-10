using Pharma.Application.Alertes;
using Pharma.Application.Commun;
using Pharma.Domain.Alertes;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

/// <summary>Réglage général (table parametre_alerte).</summary>
public sealed record ParametresAlerte(
    int JoursOrange, int JoursRouge, int HorizonRisqueJours,
    int DelaiFournisseurJours, int JoursSecurite, int JoursCibleCommande, int JoursDormant);

public sealed record SeuilGrossiste(long FournisseurId, string Nom, int? DelaiRepriseJours, int? JoursOrange, int? JoursRouge);
public sealed record ExceptionProduit(long ProduitId, string Nom, int? JoursOrange, int? JoursRouge);
public sealed record ReglagesSeuils(ParametresAlerte General, IReadOnlyList<SeuilGrossiste> Grossistes, IReadOnlyList<ExceptionProduit> Exceptions);

public interface ISeuilsAdmin
{
    Task<ReglagesSeuils> LireAsync(CancellationToken ct = default);

    /// <summary>Enregistre tout en une transaction ; un produit absent des exceptions revient au réglage général.</summary>
    Task EnregistrerAsync(ReglagesSeuils reglages, CancellationToken ct = default);
}

public sealed class ObtenirSeuils(ISeuilsAdmin seuils)
{
    public Task<ReglagesSeuils> ExecuterAsync(CancellationToken ct = default) => seuils.LireAsync(ct);
}

/// <summary>Enregistre les seuils, recalcule les alertes aussitôt et inscrit la modification au journal.</summary>
public sealed class EnregistrerSeuils(ISeuilsAdmin seuils, IMoteurAlertes alertes, IJournal journal)
{
    public async Task<int> ExecuterAsync(ReglagesSeuils r, long utilisateurId, CancellationToken ct = default)
    {
        var g = r.General;
        RegleSeuils.VerifierGeneral(g.JoursOrange, g.JoursRouge);
        if (g.HorizonRisqueJours <= 0 || g.JoursCibleCommande <= 0 || g.JoursDormant <= 0)
            throw new RegleMetierException("Le risque d’écoulement, le stock visé et le délai « dormant » doivent être d’au moins 1 jour.");
        if (g.DelaiFournisseurJours < 0 || g.JoursSecurite < 0)
            throw new RegleMetierException("Le délai de livraison et le stock de sécurité ne peuvent pas être négatifs.");

        foreach (var f in r.Grossistes)
        {
            if (f.DelaiRepriseJours is <= 0)
                throw new RegleMetierException($"{f.Nom} : le délai de reprise doit être d’au moins 1 jour (ou laissé vide).");
            RegleSeuils.VerifierParticulier(f.Nom, f.JoursOrange, f.JoursRouge, g.JoursOrange, g.JoursRouge);
        }

        // Une exception de produit n'a de sens que si au moins un seuil est saisi
        var exceptions = r.Exceptions.Where(e => e.JoursOrange is not null || e.JoursRouge is not null).ToList();
        foreach (var e in exceptions)
            RegleSeuils.VerifierParticulier(e.Nom, e.JoursOrange, e.JoursRouge, g.JoursOrange, g.JoursRouge);
        if (exceptions.Select(e => e.ProduitId).Distinct().Count() != exceptions.Count)
            throw new RegleMetierException("Un même produit figure deux fois dans les exceptions.");

        var avant = await seuils.LireAsync(ct);
        var nouveaux = r with { Exceptions = exceptions };
        await seuils.EnregistrerAsync(nouveaux, ct);
        var misesAJour = await alertes.GenererAsync(ct);

        await journal.EcrireAsync(utilisateurId, "seuils_alerte", "parametre_alerte", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = nouveaux }, Permissions.AdminParametres, ct);
        return misesAJour;
    }
}
