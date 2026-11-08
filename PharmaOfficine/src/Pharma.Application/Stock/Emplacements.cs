using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Stock;

public sealed record Emplacement(
    long Id, string Code, string Rayon, string? Etagere, string? Tiroir, string? Libelle, ZoneEmplacement Zone, bool Actif, int NombreProduits);

public sealed record SaisieEmplacement(string? Rayon, string? Etagere, string? Tiroir, string? Libelle, ZoneEmplacement Zone, bool Actif);

public interface IEmplacements
{
    Task<IReadOnlyList<Emplacement>> ListerAsync(CancellationToken ct = default);
    Task<bool> CodeExisteAsync(string code, long? saufId, CancellationToken ct = default);
    Task<long> CreerAsync(string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, CancellationToken ct = default);
    Task<bool> ModifierAsync(long id, string code, string rayon, string? etagere, string? tiroir, string? libelle, ZoneEmplacement zone, bool actif, CancellationToken ct = default);
    Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default);
    Task RangerAsync(long produitId, long? emplacementId, long? reserveId, CancellationToken ct = default);
}

public sealed class ListerEmplacements(IEmplacements emplacements)
{
    public Task<IReadOnlyList<Emplacement>> ExecuterAsync(CancellationToken ct = default) => emplacements.ListerAsync(ct);
}

/// <summary>Création ou modification d'un emplacement (droit admin.parametres), inscrite au journal.</summary>
public sealed class EnregistrerEmplacement(IEmplacements emplacements, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long? id, SaisieEmplacement s, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "gérer les emplacements", ct);
        var (rayon, etagere, tiroir, code) = RegleEmplacement.Composer(s.Rayon, s.Etagere, s.Tiroir);
        var libelle = string.IsNullOrWhiteSpace(s.Libelle) ? null : s.Libelle.Trim();
        if (await emplacements.CodeExisteAsync(code, id, ct))
            throw new RegleMetierException($"L’emplacement {code} existe déjà.");

        if (id is null)
            id = await emplacements.CreerAsync(code, rayon, etagere, tiroir, libelle, s.Zone, ct);
        else if (!await emplacements.ModifierAsync(id.Value, code, rayon, etagere, tiroir, libelle, s.Zone, s.Actif, ct))
            throw new RegleMetierException("Emplacement introuvable.");

        await journal.EcrireAsync(utilisateurId, "emplacement", "emplacement", id,
            new Dictionary<string, object?> { ["code"] = code, ["libelle"] = libelle, ["zone"] = s.Zone.Code(), ["actif"] = s.Actif },
            Permissions.AdminParametres, ct);
        return id.Value;
    }
}

/// <summary>Range un produit : emplacement de vente et réserve facultative (droit stock.ajuster).</summary>
public sealed class RangerProduit(IEmplacements emplacements, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long produitId, long? emplacementId, long? reserveId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockAjuster, "ranger un produit", ct);
        var nom = await emplacements.NomProduitAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        var liste = await emplacements.ListerAsync(ct);
        Emplacement? Trouver(long? eid) => eid is null ? null
            : liste.FirstOrDefault(e => e.Id == eid && e.Actif) ?? throw new RegleMetierException("Emplacement introuvable ou désactivé.");
        var (vente, reserve) = (Trouver(emplacementId), Trouver(reserveId));
        RegleEmplacement.VerifierRangement(vente?.Zone, reserve?.Zone, emplacementId, reserveId);

        await emplacements.RangerAsync(produitId, emplacementId, reserveId, ct);
        await journal.EcrireAsync(utilisateurId, "produit_range", "produit", produitId,
            new Dictionary<string, object?> { ["produit"] = nom, ["vente"] = vente?.Code, ["reserve"] = reserve?.Code },
            Permissions.StockAjuster, ct);
    }
}
