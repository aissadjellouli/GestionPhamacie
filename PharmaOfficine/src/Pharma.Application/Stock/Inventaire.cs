using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Stock;

public sealed record ParametresInventaire(int SeuilBoites, decimal SeuilMontant);

/// <summary>Statut : a_compter, compte, conforme, ajuste, a_valider, valide ou refuse.</summary>
public sealed record LigneInventaire(
    long Id, long LotId, string Produit, string NumeroLot, DateOnly DatePeremption, bool DateAVerifier, string? Emplacement,
    decimal PrixAchatHt, int? Compte, DateOnly? DateVerifiee, string Statut, int? Theorique, int? Ecart, decimal? ValeurEcart);

public sealed record InventaireDetail(
    long Id, string Rayon, string Statut, DateTime CommenceLe, string CommencePar, DateTime? TermineLe, IReadOnlyList<LigneInventaire> Lignes);

public sealed record InventaireResume(long Id, string Rayon, string Statut, DateTime CommenceLe, string CommencePar, DateTime? TermineLe,
    int Lignes, int Ecarts, int AValider, decimal ValeurEcarts);

public sealed record EcartAValider(
    long LigneId, long InventaireId, string Rayon, string Produit, string NumeroLot, int Theorique, int Compte, int Ecart,
    decimal ValeurEcart, string ComptePar, DateTime CompteLe);

/// <summary>Résultat d'une ligne au moment de terminer : écart, valeur et décision.</summary>
public sealed record DecisionLigne(long LigneId, int Ecart, decimal ValeurEcart, DecisionEcart Decision);

public interface IInventaires
{
    Task<ParametresInventaire> ParametresAsync(CancellationToken ct = default);
    Task ModifierParametresAsync(ParametresInventaire p, CancellationToken ct = default);
    Task<IReadOnlyList<RayonInventaire>> RayonsAsync(CancellationToken ct = default);
    /// <summary>Crée l'inventaire et une ligne par lot en stock rangé dans le rayon (vente ou réserve).</summary>
    Task<long> CreerAsync(string rayon, long utilisateurId, CancellationToken ct = default);
    Task<InventaireDetail?> DetailAsync(long id, CancellationToken ct = default);
    /// <summary>Enregistre le comptage et relève le stock théorique du lot à cet instant.</summary>
    Task EnregistrerComptageAsync(long ligneId, int compte, DateOnly? dateVerifiee, long utilisateurId, CancellationToken ct = default);
    /// <summary>Lot désigné par une étiquette interne, un code de lot ou un Data Matrix (GTIN et lot) ; null sinon.</summary>
    Task<long?> LotParCodeAsync(string code, CancellationToken ct = default);
    /// <summary>Écarts, ajustements sous les seuils, dates vérifiées et fin de l'inventaire, en une transaction.</summary>
    Task TerminerAsync(long id, IReadOnlyList<DecisionLigne> decisions, long utilisateurId, CancellationToken ct = default);
    Task AnnulerAsync(long id, CancellationToken ct = default);
    Task<IReadOnlyList<EcartAValider>> EcartsAValiderAsync(CancellationToken ct = default);
    /// <summary>Valider applique l'ajustement au stock actuel ; refuser laisse le stock (recompter).</summary>
    Task DeciderAsync(long ligneId, bool valider, long utilisateurId, CancellationToken ct = default);
    Task<IReadOnlyList<InventaireResume>> HistoriqueAsync(int limite, CancellationToken ct = default);
}

public sealed record TableauInventaire(
    IReadOnlyList<RayonInventaire> Rayons, RayonInventaire? Propose, ParametresInventaire Parametres,
    IReadOnlyList<EcartAValider> EcartsAValider, IReadOnlyList<InventaireResume> Historique);

public sealed class ObtenirTableauInventaire(IInventaires inventaires)
{
    public async Task<TableauInventaire> ExecuterAsync(CancellationToken ct = default)
    {
        var rayons = await inventaires.RayonsAsync(ct);
        return new TableauInventaire(rayons, RegleInventaire.Proposer(rayons), await inventaires.ParametresAsync(ct),
            await inventaires.EcartsAValiderAsync(ct), await inventaires.HistoriqueAsync(20, ct));
    }
}

/// <summary>Commence (ou reprend) l'inventaire d'un rayon.</summary>
public sealed class CommencerInventaire(IInventaires inventaires, IPermissionService droits)
{
    public async Task<long> ExecuterAsync(string? rayon, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "faire un inventaire", ct);
        var r = (await inventaires.RayonsAsync(ct)).FirstOrDefault(x => string.Equals(x.Rayon, rayon?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new RegleMetierException("Rayon inconnu.");
        if (r.EnCoursId is { } enCours) return enCours;
        if (r.NombreLots == 0) throw new RegleMetierException($"Aucun lot en stock n’est rangé dans le rayon {r.Rayon}.");
        return await inventaires.CreerAsync(r.Rayon, utilisateurId, ct);
    }
}

/// <summary>
/// Inventaire vu par l'employé. Pendant le comptage, le stock théorique et les écarts ne sont jamais renvoyés (comptage à l'aveugle).
/// </summary>
public sealed class ObtenirInventaire(IInventaires inventaires)
{
    public async Task<InventaireDetail?> ExecuterAsync(long id, CancellationToken ct = default)
    {
        if (await inventaires.DetailAsync(id, ct) is not { } d) return null;
        return d.Statut != "en_cours" ? d
            : d with { Lignes = d.Lignes.Select(l => l with { Theorique = null, Ecart = null, ValeurEcart = null }).ToList() };
    }
}

public sealed class CompterLot(IInventaires inventaires, IPermissionService droits, IHorloge horloge)
{
    public async Task ExecuterAsync(long inventaireId, long ligneId, int compte, DateOnly? dateVerifiee, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "faire un inventaire", ct);
        RegleInventaire.VerifierComptage(compte);
        RegleInventaire.VerifierDate(dateVerifiee, horloge.Aujourdhui);
        var d = await EnCours(inventaires, inventaireId, ct);
        var ligne = d.Lignes.FirstOrDefault(l => l.Id == ligneId) ?? throw new RegleMetierException("Ligne introuvable dans cet inventaire.");
        await inventaires.EnregistrerComptageAsync(ligne.Id, compte, ligne.DateAVerifier ? dateVerifiee : null, utilisateurId, ct);
    }

    internal static async Task<InventaireDetail> EnCours(IInventaires inventaires, long id, CancellationToken ct)
    {
        var d = await inventaires.DetailAsync(id, ct) ?? throw new RegleMetierException("Inventaire introuvable.");
        if (d.Statut != "en_cours") throw new RegleMetierException("Cet inventaire est terminé.");
        return d;
    }
}

/// <summary>Scan d'une boîte pendant le comptage : une boîte de plus sur la ligne de son lot.</summary>
public sealed class ScannerBoiteInventaire(IInventaires inventaires, IPermissionService droits)
{
    public async Task<(long LigneId, string Produit, string NumeroLot, int Compte)> ExecuterAsync(long inventaireId, string? code, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "faire un inventaire", ct);
        if (string.IsNullOrWhiteSpace(code)) throw new RegleMetierException("Code vide.");
        var d = await CompterLot.EnCours(inventaires, inventaireId, ct);
        var lotId = await inventaires.LotParCodeAsync(code.Trim(), ct)
                    ?? throw new RegleMetierException("Ce code ne désigne pas un lot précis : saisissez la quantité à la main.");
        var ligne = d.Lignes.FirstOrDefault(l => l.LotId == lotId)
                    ?? throw new RegleMetierException($"Cette boîte n’appartient pas au rayon {d.Rayon} : rangez-la à sa place.");
        var compte = (ligne.Compte ?? 0) + 1;
        RegleInventaire.VerifierComptage(compte);
        await inventaires.EnregistrerComptageAsync(ligne.Id, compte, null, utilisateurId, ct);
        return (ligne.Id, ligne.Produit, ligne.NumeroLot, compte);
    }
}

public sealed record BilanInventaire(int Lignes, int Conformes, int Ajustes, int AValider, int BoitesEcart, decimal ValeurEcart);

/// <summary>
/// Fin du comptage : chaque écart est ajusté tout de suite s'il reste sous les seuils, sinon il attend le titulaire,
/// prévenu par SMS ou e-mail.
/// </summary>
public sealed class TerminerInventaire(IInventaires inventaires, IPermissionService droits, IJournal journal, IAlertesTitulaire? alertes = null)
{
    public async Task<BilanInventaire> ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "terminer un inventaire", ct);
        var d = await CompterLot.EnCours(inventaires, id, ct);
        var restants = d.Lignes.Count(l => l.Compte is null);
        if (restants > 0)
            throw new RegleMetierException($"{restants} lot(s) pas encore compté(s) : saisissez 0 si le lot est introuvable.");

        var p = await inventaires.ParametresAsync(ct);
        var decisions = d.Lignes.Select(l =>
        {
            var ecart = l.Compte!.Value - (l.Theorique ?? 0);
            var valeur = Math.Round(ecart * l.PrixAchatHt, 2);
            return new DecisionLigne(l.Id, ecart, valeur, RegleInventaire.Decider(ecart, valeur, p.SeuilBoites, p.SeuilMontant));
        }).ToList();

        await inventaires.TerminerAsync(id, decisions, utilisateurId, ct);
        var aValider = decisions.Where(x => x.Decision == DecisionEcart.AValider).ToList();
        var bilan = new BilanInventaire(decisions.Count, decisions.Count(x => x.Decision == DecisionEcart.Conforme),
            decisions.Count(x => x.Decision == DecisionEcart.Ajuste), aValider.Count,
            decisions.Sum(x => x.Ecart), decisions.Sum(x => x.ValeurEcart));

        await journal.EcrireAsync(utilisateurId, "inventaire_termine", "inventaire", id,
            new Dictionary<string, object?>
            {
                ["rayon"] = d.Rayon, ["lots"] = bilan.Lignes, ["ajustes"] = bilan.Ajustes, ["a_valider"] = bilan.AValider,
                ["boites_ecart"] = bilan.BoitesEcart, ["valeur_ecart"] = bilan.ValeurEcart
            }, Permissions.StockAjuster, ct);

        if (alertes is not null && aValider.Count > 0)
        {
            try { await alertes.EcartsInventaireAsync(d.Rayon, aValider.Count, aValider.Sum(x => x.Ecart), aValider.Sum(x => x.ValeurEcart), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return bilan;
    }
}

public sealed class AnnulerInventaire(IInventaires inventaires, IPermissionService droits)
{
    public async Task ExecuterAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "annuler un inventaire", ct);
        await CompterLot.EnCours(inventaires, id, ct);
        await inventaires.AnnulerAsync(id, ct);
    }
}

/// <summary>Écart au-delà des seuils : validé (stock ajusté) ou refusé (à recompter) par le titulaire seulement.</summary>
public sealed class DeciderEcartInventaire(IInventaires inventaires, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long ligneId, bool valider, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "valider un écart d’inventaire", ct);
        var ecart = (await inventaires.EcartsAValiderAsync(ct)).FirstOrDefault(e => e.LigneId == ligneId)
                    ?? throw new RegleMetierException("Cet écart a déjà été traité.");
        await inventaires.DeciderAsync(ligneId, valider, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, valider ? "ecart_inventaire_valide" : "ecart_inventaire_refuse", "ligne_inventaire", ligneId,
            new Dictionary<string, object?>
            {
                ["rayon"] = ecart.Rayon, ["produit"] = ecart.Produit, ["lot"] = ecart.NumeroLot,
                ["theorique"] = ecart.Theorique, ["compte"] = ecart.Compte, ["ecart"] = ecart.Ecart, ["valeur"] = ecart.ValeurEcart
            }, Permissions.StockAjuster, ct);
    }
}

public sealed class ModifierParametresInventaire(IInventaires inventaires, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(ParametresInventaire p, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les seuils d’inventaire", ct);
        if (p.SeuilBoites < 0 || p.SeuilMontant < 0) throw new RegleMetierException("Les seuils ne peuvent pas être négatifs.");
        await inventaires.ModifierParametresAsync(p, ct);
        await journal.EcrireAsync(utilisateurId, "seuils_inventaire", "parametre_inventaire", 1,
            new Dictionary<string, object?> { ["seuil_boites"] = p.SeuilBoites, ["seuil_montant"] = p.SeuilMontant }, Permissions.AdminParametres, ct);
    }
}
