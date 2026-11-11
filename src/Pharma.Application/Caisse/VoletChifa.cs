using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Caisse;

// ---- Reprise d'une vente Chifa par le QR code du volet (éviter la double saisie) ----
// Format du QR déduit d'un seul volet réel : à confirmer avec l'éditeur de Chifa (docs/chifa.md, docs/a-faire.md).

/// <summary>Produit du catalogue associé à un code Chifa : nom et prix public.</summary>
public sealed record ProduitChifa(long Id, string Nom, decimal PrixPublic);

/// <summary>Ligne du volet ; Produit null : code Chifa pas encore associé à un produit du catalogue.</summary>
public sealed record LigneVoletChifa(string Code, int Quantite, decimal PrixUnitaire, ProduitChifa? Produit);

/// <summary>Volet Chifa lu à la caisse : facture, assuré (et patient retrouvé), lignes rapprochées du catalogue.</summary>
/// <summary>PatientNumAssure : n° d'assuré tel qu'enregistré dans la fiche du patient retrouvé.</summary>
public sealed record VoletChifaLu(QrChifa Qr, IReadOnlyList<LigneVoletChifa> Lignes, long? PatientId, string? Patient, long? OrganismeId,
    string? PatientNumAssure = null);

public interface IVoletsChifa
{
    /// <summary>Numéro de la vente (non annulée) qui a déjà repris ce volet, sinon null.</summary>
    Task<string?> VenteDuVoletAsync(string numeroFacture, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, ProduitChifa>> ProduitsAsync(IEnumerable<string> codes, CancellationToken ct = default);
    /// <summary>Patient dont le n° d'assuré (chiffres seuls) vaut le n° et son suffixe, ou le n° seul.</summary>
    Task<(long Id, string Nom, string NumAssure)?> PatientAsync(string numeroAssure, string suffixe, CancellationToken ct = default);
    Task<long?> OrganismeAsync(string code, CancellationToken ct = default);
    /// <summary>Associe (ou réassocie) un code Chifa à un produit actif ; nom du produit et ancien produit associé, ou null si le produit n'existe pas.</summary>
    Task<(string Produit, string? Ancien)?> AssocierAsync(string code, long produitId, long utilisateurId, CancellationToken ct = default);
}

public static class LectureVoletChifa
{
    /// <summary>Texte scanné relu (douchette réglée en QWERTY sur un poste AZERTY), puis lu comme un QR de volet Chifa.</summary>
    public static QrChifa? Lire(string? texte) =>
        QrChifa.TryParse(Achats.RegleDocumentReception.Normaliser(texte), out var qr) ? qr : null;
}

/// <summary>QR du volet scanné à la caisse : la vente est reprise (assuré, produits, quantités) sans ressaisie, en tiers payant CNAS.</summary>
public sealed class LireVoletChifa(IVoletsChifa volets, IPermissionService droits)
{
    public async Task<VoletChifaLu> ExecuterAsync(string? texte, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "reprendre un volet Chifa", ct);
        var qr = LectureVoletChifa.Lire(texte)
                 ?? throw new RegleMetierException("Ce code n’est pas un QR de volet Chifa reconnu.", "volet_chifa");
        if (await volets.VenteDuVoletAsync(qr.NumeroFacture, ct) is { } vente)
            throw new RegleMetierException($"Le volet Chifa n° {qr.NumeroFacture} a déjà été repris par la vente {vente}.", "volet_chifa");
        var produits = await volets.ProduitsAsync(qr.Lignes.Select(l => l.Code).Distinct(), ct);
        var patient = await volets.PatientAsync(qr.NumeroAssure, qr.SuffixeAssure, ct);
        return new VoletChifaLu(qr, qr.Lignes.Select(l => new LigneVoletChifa(l.Code, l.Quantite, l.PrixUnitaire, produits.GetValueOrDefault(l.Code))).ToList(),
            patient?.Id, patient?.Nom, await volets.OrganismeAsync("CNAS", ct), patient?.NumAssure);
    }
}

/// <summary>Code produit Chifa associé à un produit du catalogue (une fois, puis reconnu à chaque volet) ; inscrit au journal.</summary>
public sealed class AssocierCodeChifa(IVoletsChifa volets, IPermissionService droits, IJournal journal)
{
    public async Task<string> ExecuterAsync(string? code, long produitId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CaisseVendre, "associer un code Chifa à un produit", ct);
        var c = code?.Trim() ?? "";
        if (c.Length is 0 or > 20 || !c.All(char.IsAsciiDigit))
            throw new RegleMetierException("Code Chifa invalide : des chiffres seulement (20 au plus).", "code_chifa");
        var (produit, ancien) = await volets.AssocierAsync(c, produitId, utilisateurId, ct)
                                ?? throw new RegleMetierException("Produit introuvable ou inactif.", "code_chifa");
        await journal.EcrireAsync(utilisateurId, "code_chifa_associe", "produit", produitId,
            new Dictionary<string, object?> { ["code"] = c, ["produit"] = ancien is null || ancien == produit ? produit : new { avant = ancien, apres = produit } },
            Permissions.CaisseVendre, ct);
        return produit;
    }
}
