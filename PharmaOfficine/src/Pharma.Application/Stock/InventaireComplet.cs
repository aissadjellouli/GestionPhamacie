using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Stock;

public sealed record CampagneInventaire(long Id, string Mode, string Statut, DateTime CommenceLe, string CommencePar, DateTime? ClotureLe,
    string? CloturePar, int? NombreLots, int? NombreBoites, decimal? ValeurStock, string? Commentaire);

public sealed record LigneEtatInventaire(string Produit, string NumeroLot, DateOnly DatePeremption, string Rayon, int Quantite, decimal PrixAchatHt, decimal Valeur);

public sealed record AvancementCampagne(CampagneInventaire Campagne, IReadOnlyList<RayonCampagne> Rayons);

public sealed record EtatInventaire(CampagneInventaire Campagne, IReadOnlyList<LigneEtatInventaire> Lignes, int Ecarts, decimal ValeurEcarts);

public interface ICampagnesInventaire
{
    Task<CampagneInventaire?> EnCoursAsync(CancellationToken ct = default);
    /// <summary>Refuse si un inventaire tournant est déjà en cours (à terminer ou annuler d'abord).</summary>
    Task<long> CommencerAsync(string mode, long utilisateurId, CancellationToken ct = default);
    Task<IReadOnlyList<RayonCampagne>> RayonsAsync(long campagneId, CancellationToken ct = default);
    /// <summary>Fige l'état (stock par lot au prix d'achat) et clôture, en une transaction.</summary>
    Task CloturerAsync(long campagneId, long utilisateurId, string? commentaire, CancellationToken ct = default);
    /// <summary>Annule la campagne et ses inventaires de rayon encore en cours ; les rayons terminés gardent leurs corrections.</summary>
    Task AnnulerAsync(long campagneId, CancellationToken ct = default);
    Task<EtatInventaire?> EtatAsync(long campagneId, CancellationToken ct = default);
    Task<IReadOnlyList<CampagneInventaire>> HistoriqueAsync(CancellationToken ct = default);
}

public interface IDocumentsInventaire
{
    byte[] EtatInventaire(EtatInventaire etat, string? pharmacie);
}

/// <summary>Garde-fou de la caisse : vrai pendant un inventaire complet « pharmacie fermée ».</summary>
public static class VentesSuspendues
{
    public static async Task VerifierAsync(ICampagnesInventaire? campagnes, CancellationToken ct)
    {
        if (campagnes is not null && await campagnes.EnCoursAsync(ct) is { Mode: "fermee" })
            throw new RegleMetierException("Inventaire complet en cours, pharmacie fermée : les ventes reprennent à la clôture de l’inventaire.");
    }
}

public sealed class ConsulterInventaireComplet(ICampagnesInventaire campagnes)
{
    public async Task<AvancementCampagne?> EnCoursAsync(CancellationToken ct = default) =>
        await campagnes.EnCoursAsync(ct) is { } c ? new AvancementCampagne(c, await campagnes.RayonsAsync(c.Id, ct)) : null;

    public Task<IReadOnlyList<CampagneInventaire>> HistoriqueAsync(CancellationToken ct = default) => campagnes.HistoriqueAsync(ct);
}

/// <summary>Lancement, clôture et annulation de l'inventaire complet : réservés au titulaire.</summary>
public sealed class GererInventaireComplet(ICampagnesInventaire campagnes, IPermissionService droits, IJournal journal)
{
    public async Task<long> CommencerAsync(string? mode, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "lancer l’inventaire complet", ct);
        var m = RegleInventaireComplet.Mode(mode);
        if (await campagnes.EnCoursAsync(ct) is not null) throw new RegleMetierException("Un inventaire complet est déjà en cours.");
        var id = await campagnes.CommencerAsync(m, utilisateurId, ct);
        await journal.EcrireAsync(utilisateurId, "inventaire_complet_commence", "campagne_inventaire", id,
            new Dictionary<string, object?> { ["mode"] = m }, null, ct);
        return id;
    }

    public async Task<EtatInventaire> CloturerAsync(long id, string? commentaire, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "clôturer l’inventaire complet", ct);
        var c = await campagnes.EnCoursAsync(ct);
        if (c?.Id != id) throw new RegleMetierException("Cet inventaire complet n’est plus en cours.");
        RegleInventaireComplet.VerifierCloture(await campagnes.RayonsAsync(id, ct));
        await campagnes.CloturerAsync(id, utilisateurId, string.IsNullOrWhiteSpace(commentaire) ? null : commentaire.Trim(), ct);
        var etat = await campagnes.EtatAsync(id, ct) ?? throw new InvalidOperationException("État d'inventaire introuvable.");
        await journal.EcrireAsync(utilisateurId, "inventaire_complet_cloture", "campagne_inventaire", id, new Dictionary<string, object?>
        {
            ["lots"] = etat.Campagne.NombreLots, ["boites"] = etat.Campagne.NombreBoites, ["valeur"] = etat.Campagne.ValeurStock,
            ["ecarts"] = etat.Ecarts, ["valeur_ecarts"] = etat.ValeurEcarts
        }, null, ct);
        return etat;
    }

    public async Task AnnulerAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "annuler l’inventaire complet", ct);
        if ((await campagnes.EnCoursAsync(ct))?.Id != id) throw new RegleMetierException("Cet inventaire complet n’est plus en cours.");
        await campagnes.AnnulerAsync(id, ct);
        await journal.EcrireAsync(utilisateurId, "inventaire_complet_annule", "campagne_inventaire", id, new Dictionary<string, object?>(), null, ct);
    }

    public async Task<(byte[] Contenu, string Nom)> EtatPdfAsync(long id, IDocumentsInventaire pdf, string? pharmacie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "imprimer l’état d’inventaire", ct);
        var etat = await campagnes.EtatAsync(id, ct) ?? throw new RegleMetierException("Inventaire introuvable.");
        if (etat.Campagne.Statut != "cloturee") throw new RegleMetierException("L’état d’inventaire s’imprime après la clôture.");
        return (pdf.EtatInventaire(etat, pharmacie), $"etat_inventaire_{etat.Campagne.ClotureLe:yyyy-MM-dd}.pdf");
    }
}
