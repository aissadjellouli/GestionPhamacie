using Pharma.Domain.Commun;

namespace Pharma.Application.Caisse;

/// <summary>Vente mise en attente sur la caisse du poste. Contenu : panier du poste (JSON), recalculé à la reprise.</summary>
public sealed record VenteEnAttente(long Id, string Libelle, int Lignes, decimal Total, string Employe, DateTime CreeLe);

public interface IVentesEnAttente
{
    Task<IReadOnlyList<VenteEnAttente>> ListerAsync(string poste, CancellationToken ct = default);
    Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, int lignes, decimal total, string contenu, CancellationToken ct = default);
    /// <summary>Contenu du panier, retiré de l'attente ; null s'il n'existe pas sur ce poste (déjà repris).</summary>
    Task<string?> ReprendreAsync(long id, string poste, CancellationToken ct = default);
    Task<bool> SupprimerAsync(long id, string poste, CancellationToken ct = default);
}

/// <summary>Mettre de côté, reprendre ou abandonner une vente du poste (droit caisse.vendre).</summary>
public sealed class GererVentesEnAttente(IVentesEnAttente attente)
{
    public const int MaxParPoste = 20;
    public const int TailleMax = 200_000;

    public Task<IReadOnlyList<VenteEnAttente>> ListerAsync(string? poste, CancellationToken ct = default) =>
        attente.ListerAsync(PosteCaisse.Exiger(poste), ct);

    public async Task<long> MettreEnAttenteAsync(string? poste, long utilisateurId, string? libelle, int lignes, decimal total,
        string? contenu, CancellationToken ct = default)
    {
        var p = PosteCaisse.Exiger(poste);
        if (lignes <= 0 || string.IsNullOrWhiteSpace(contenu)) throw new RegleMetierException("Panier vide : rien à mettre en attente.");
        if (contenu.Length > TailleMax) throw new RegleMetierException("Panier trop volumineux pour être mis en attente.");
        if ((await attente.ListerAsync(p, ct)).Count >= MaxParPoste)
            throw new RegleMetierException($"Déjà {MaxParPoste} ventes en attente sur ce poste : reprenez-en ou supprimez-en une.");
        var nom = string.IsNullOrWhiteSpace(libelle) ? "Vente en attente" : libelle.Trim();
        return await attente.EnregistrerAsync(p, utilisateurId, nom.Length > 80 ? nom[..80] : nom, lignes, total, contenu, ct);
    }

    public async Task<string> ReprendreAsync(long id, string? poste, CancellationToken ct = default) =>
        await attente.ReprendreAsync(id, PosteCaisse.Exiger(poste), ct)
        ?? throw new RegleMetierException("Cette vente en attente n’existe plus (déjà reprise par un collègue ?).");

    public async Task SupprimerAsync(long id, string? poste, CancellationToken ct = default)
    {
        if (!await attente.SupprimerAsync(id, PosteCaisse.Exiger(poste), ct))
            throw new RegleMetierException("Cette vente en attente n’existe plus.");
    }
}
