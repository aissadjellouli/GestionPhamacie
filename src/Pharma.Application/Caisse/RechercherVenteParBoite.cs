using Pharma.Application.Commun;
using Pharma.Application.Mobile;
using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Application.Caisse;

/// <summary>
/// Ventes trouvées en scannant la boîte rapportée. Exacte : la boîte (étiquette interne ou n° de série) a été rattachée
/// à sa vente à l'encaissement ; sinon, ventes de la période qui contiennent ce produit (et ce lot s'il est connu).
/// </summary>
public sealed record VentesParBoite(bool Exacte, string Produit, string? Lot, string? Note, IReadOnlyList<VenteAnnulable> Ventes);

public interface IVentesParBoite
{
    /// <summary>Vente encore validée dans laquelle cette boîte est partie (null : boîte non rattachée à une vente).</summary>
    Task<long?> VenteDeLaBoiteAsync(long boiteId, CancellationToken ct = default);
    /// <summary>
    /// Ventes validées depuis la date, qui contiennent ce produit (et ce lot) avec au moins une boîte pas encore rendue.
    /// PatientId : ventes de ce patient ; null : ventes sans patient seulement (on ne rembourse pas la vente d'un autre patient).
    /// </summary>
    Task<IReadOnlyList<VenteAnnulable>> ParProduitAsync(long produitId, long? lotId, string? numeroLot, DateOnly depuis, long? patientId,
        CancellationToken ct = default);
    Task<string?> NomProduitAsync(long produitId, CancellationToken ct = default);
    /// <summary>Dernier mouvement de la boîte entre vente et retour : un retour client (elle a déjà été rendue).</summary>
    Task<bool> DejaRendueAsync(long boiteId, CancellationToken ct = default);
}

/// <summary>
/// Remboursement sans ticket : le caissier scanne la boîte rapportée. Le code-barres du fabricant désigne le produit (toutes
/// ses boîtes portent le même) ; un Data Matrix donne aussi le lot ; une étiquette interne ou un n° de série désigne la boîte.
/// </summary>
public sealed class RechercherVenteParBoite(IMobile codes, IVentesParBoite ventes, IAnnulationsVente annulations, IHorloge horloge)
{
    public const int JoursParDefaut = 7;

    public async Task<VentesParBoite> ExecuterAsync(string? code, int? jours, long? patientId, string? numeroLot, CancellationToken ct = default)
    {
        var brut = code?.Trim() ?? "";
        if (brut.Length == 0) throw new RegleMetierException("Scannez la boîte rapportée (ou tapez son code-barres).", "code_boite");
        var periode = Math.Clamp(jours ?? JoursParDefaut, 1, 365);

        string? lotSaisi = string.IsNullOrWhiteSpace(numeroLot) ? null : RegleLot.Normaliser(numeroLot);
        CodeResolu? resolu;
        if (Gs1DataMatrix.TryParse(brut, out var dm) && dm is not null)
        {
            lotSaisi ??= dm.Lot is null ? null : RegleLot.Normaliser(dm.Lot);
            resolu = await codes.ResoudreDataMatrixAsync(dm.Gtin, lotSaisi, dm.NumeroSerie, ct);
        }
        else resolu = await codes.ResoudreCodeAsync(brut, ct);
        if (resolu is null) throw new RegleMetierException("Code inconnu : aucun produit de la pharmacie ne porte ce code.", "code_boite");
        var produit = await ventes.NomProduitAsync(resolu.ProduitId, ct) ?? "Produit";

        // Boîte identifiée et rattachée à sa vente : c'est cette vente, et aucune autre
        string? note = null;
        if (resolu.BoiteId is { } boite)
        {
            switch (resolu.StatutBoite)
            {
                case "vendue" when await ventes.VenteDeLaBoiteAsync(boite, ct) is { } venteId:
                    var v = await annulations.TrouverAsync(venteId, null, ct);
                    return new VentesParBoite(true, produit, null, null, v is null ? [] : [v]);
                case "en_stock" or "detruite" or "retour_client" when await ventes.DejaRendueAsync(boite, ct):
                    throw new RegleMetierException("Cette boîte a déjà été rendue par un patient : elle ne peut pas être remboursée une seconde fois.", "code_boite");
                case "detruite" or "retournee_fournisseur" or "donnee":
                    throw new RegleMetierException("Cette boîte est sortie du stock (détruite, rendue au grossiste ou donnée) : elle ne peut pas être remboursée.", "code_boite");
            }
            // Boîte encore « en stock » : vendue avant que la caisse ne retienne les boîtes, ou vendue sans être scannée
            note = "Boîte non rattachée à une vente : recherche par produit et lot.";
        }

        var lotId = resolu.Type is "boite" or "lot" ? resolu.LotId : null;
        var depuis = horloge.Aujourdhui.AddDays(-periode);
        var trouvees = await ventes.ParProduitAsync(resolu.ProduitId, lotId, lotId is null ? lotSaisi : null, depuis, patientId, ct);
        return new VentesParBoite(false, produit, lotSaisi, note, trouvees);
    }
}
