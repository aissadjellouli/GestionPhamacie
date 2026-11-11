using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>Blocage d'un produit : à l'achat (ni commande, ni réception), à la vente (refusé en caisse), ou les deux.</summary>
public sealed record BlocageProduit(long ProduitId, string Nom, bool BloqueAchat, bool BloqueVente, string? Motif);

/// <summary>Accord pour vendre ou réceptionner un produit bloqué : l'employé habilité confirme, sinon un habilité valide avec son code comptoir.</summary>
public sealed record ValidationBlocage(bool Accepte, string? ValidateurIdentifiant = null, string? ValidateurCode = null);

/// <summary>
/// Produit bloqué au panier ou dans une réception : il ne passe qu'avec la validation d'une personne qui a le droit « produits.bloquer »
/// (l'employé lui-même s'il l'a, sinon le pharmacien ou l'assistant avec son code comptoir). Renvoie qui a validé.
/// </summary>
public static class AutorisationBlocage
{
    public static async Task<(long Id, string NomComplet)?> ExigerAsync(IPermissionService droits, Securite.VerificationCodeComptoir? codes,
        long utilisateurId, ValidationBlocage? v, string? poste, string operation, IReadOnlyList<string> produits, CancellationToken ct = default)
    {
        if (produits.Count == 0) return null;
        var liste = string.Join(", ", produits);
        if (v is not { Accepte: true })
            throw new RegleMetierException($"{liste} : produit bloqué {operation}. Une personne habilitée doit valider pour passer outre.", "produit_bloque");
        if (await droits.APermissionAsync(utilisateurId, Permissions.ProduitsBloquer, ct)) return (utilisateurId, "");
        if (string.IsNullOrWhiteSpace(v.ValidateurIdentifiant) || codes is null)
            throw new RegleMetierException($"{liste} : produit bloqué {operation}. Le pharmacien ou l’assistant doit valider avec son code comptoir.", "produit_bloque");
        var c = await codes.VerifierAsync(v.ValidateurIdentifiant, v.ValidateurCode, poste, ct);
        if (!await droits.APermissionAsync(c.Id, Permissions.ProduitsBloquer, ct))
            throw new RegleMetierException($"{c.NomComplet} n’a pas le droit de valider un produit bloqué.", "produit_bloque");
        return (c.Id, c.NomComplet);
    }
}

public interface IBlocageProduits
{
    Task<BlocageProduit?> LireAsync(long produitId, CancellationToken ct = default);
    Task<IReadOnlyList<BlocageProduit>> BloquesAsync(CancellationToken ct = default);
    Task<bool> ModifierAsync(long produitId, bool bloqueAchat, bool bloqueVente, string? motif, CancellationToken ct = default);
}

public sealed class LireBlocageProduit(IBlocageProduits blocages)
{
    public async Task<BlocageProduit> ExecuterAsync(long produitId, CancellationToken ct = default) =>
        await blocages.LireAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");

    public Task<IReadOnlyList<BlocageProduit>> BloquesAsync(CancellationToken ct = default) => blocages.BloquesAsync(ct);
}

/// <summary>
/// Bloque ou débloque un produit à l'achat et/ou à la vente (droit « produits.bloquer » : titulaire et assistant par défaut).
/// Un blocage demande un motif ; chaque changement est inscrit au journal.
/// </summary>
public sealed class ModifierBlocageProduit(IBlocageProduits blocages, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long produitId, bool bloqueAchat, bool bloqueVente, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.ProduitsBloquer, "bloquer un produit", ct);
        var actuel = await blocages.LireAsync(produitId, ct) ?? throw new RegleMetierException("Produit introuvable.");
        var m = string.IsNullOrWhiteSpace(motif) ? null : motif.Trim();
        if ((bloqueAchat || bloqueVente) && m is null)
            throw new RegleMetierException("Indiquez le motif du blocage (rappel, litige, arrêt de commercialisation…).", "motif_blocage");
        if (m is { Length: > 300 }) throw new RegleMetierException("Motif trop long (300 caractères au plus).", "motif_blocage");
        if (!bloqueAchat && !bloqueVente) m = null;

        if (!await blocages.ModifierAsync(produitId, bloqueAchat, bloqueVente, m, ct)) throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, bloqueAchat || bloqueVente ? "produit_bloque" : "produit_debloque", "produit", produitId,
            new Dictionary<string, object?>
            {
                ["produit"] = actuel.Nom, ["achat"] = bloqueAchat, ["vente"] = bloqueVente, ["motif"] = m,
                ["avant_achat"] = actuel.BloqueAchat, ["avant_vente"] = actuel.BloqueVente
            }, Permissions.ProduitsBloquer, ct);
    }
}
