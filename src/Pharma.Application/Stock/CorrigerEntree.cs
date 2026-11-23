using System.Globalization;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Stock;

/// <summary>
/// Entrée de stock (un lot) en consultation : PPA (null : prix du catalogue), SHP par boîte (script 103), quantité restante,
/// boîtes identifiées encore en stock, nombre de corrections déjà faites.
/// </summary>
public sealed record EntreeStock(long LotId, long ProduitId, string Produit, string NumeroLot, DateOnly DatePeremption, int Quantite,
    decimal? Ppa, decimal Shp, long FournisseurId, string Fournisseur, DateTime DateReception, bool Bloque, int Corrections,
    int BoitesEnStock = 0);

/// <summary>Filtre de la liste des entrées de stock : produit, DCI ou n° de lot ; épuisés compris ou non.</summary>
public sealed record FiltreEntreesStock(string? Recherche = null, bool AvecEpuises = false);

/// <summary>Une ligne du journal correction_lot (une par champ corrigé).</summary>
public sealed record CorrectionLot(long Id, string Champ, string? AncienneValeur, string? NouvelleValeur, string? Motif,
    DateTimeOffset CorrigeLe, string CorrigePar);

/// <summary>Correction demandée : seuls les champs non nuls sont corrigés.</summary>
public sealed record DemandeCorrectionLot(decimal? Ppa, string? NumeroLot, DateOnly? DatePeremption, decimal? Shp, int? QuantiteRestante, string? Motif);

public sealed record ChampCorrige(string Champ, string? AncienneValeur, string? NouvelleValeur);

/// <summary>
/// Correction à écrire en une transaction : le lot (si sa quantité est toujours QuantiteAvant), un mouvement de stock
/// « ajustement » pour une quantité, une ligne de journal correction_lot par champ.
/// </summary>
public sealed record CorrectionAEcrire(long LotId, int QuantiteAvant, decimal? Ppa, string? NumeroLot, DateOnly? DatePeremption, decimal? Shp,
    int? Quantite, string Motif, long UtilisateurId, IReadOnlyList<ChampCorrige> Champs, bool BloquerLot = false,
    JournalDansTransaction? Journal = null);

public interface IEntreesStock
{
    Task<Page<EntreeStock>> PageAsync(FiltreEntreesStock filtre, DemandePage page, CancellationToken ct = default);
    Task<EntreeStock?> LotAsync(long lotId, CancellationToken ct = default);
    /// <summary>Un autre lot porte déjà ce produit, ce grossiste, ce n° et cette péremption (contrainte ux_lot_fournisseur_date).</summary>
    Task<bool> LotIdentiqueExisteAsync(long lotId, long produitId, long fournisseurId, string numeroLot, DateOnly peremption, CancellationToken ct = default);
    Task<IReadOnlyList<CorrectionLot>> CorrectionsAsync(long lotId, CancellationToken ct = default);
    /// <summary>Un autre lot de ce produit, bloqué (rappel), porte déjà ce n° de lot.</summary>
    Task<bool> NumeroRappeleAsync(long lotId, long produitId, string numeroLot, CancellationToken ct = default);
    Task CorrigerAsync(CorrectionAEcrire c, CancellationToken ct = default);
}

/// <summary>Stock › Entrées de stock : liste paginée et filtrée des lots, journal des corrections d'un lot.</summary>
public sealed class ConsulterEntreesStock(IEntreesStock entrees)
{
    public Task<Page<EntreeStock>> PageAsync(FiltreEntreesStock filtre, DemandePage page, CancellationToken ct = default) =>
        entrees.PageAsync(filtre with { Recherche = string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim() }, page, ct);

    public Task<IReadOnlyList<CorrectionLot>> CorrectionsAsync(long lotId, CancellationToken ct = default) => entrees.CorrectionsAsync(lotId, ct);
}

/// <summary>
/// Correction tracée d'une entrée de stock (droit stock.corriger_entree, 2026-11-15) : PPA, n° de lot, péremption, SHP, quantité
/// restante. Motif obligatoire (D-5). Chaque champ corrigé est inscrit au journal correction_lot (ancienne → nouvelle valeur),
/// jamais modifié ensuite, et au journal des actions sensibles. Une quantité passe par un mouvement de stock (ajustement, registre
/// des psychotropes tenu par la base). Un n° de lot ou une péremption qui donnerait un lot identique à un autre lot du même produit
/// et du même grossiste est refusé avant l'écriture. Les ventes restent liées au lot par son identifiant : la traçabilité (ventes du
/// lot, rappel, parcours des boîtes) suit le nouveau n° ; le PPA et le SHP corrigés ne changent pas les ventes passées.
/// </summary>
public sealed class CorrigerEntreeStock(IEntreesStock entrees, IPermissionService droits)
{
    public const int MotifMax = 200;

    public async Task<EntreeStock> ExecuterAsync(long lotId, DemandeCorrectionLot d, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.StockCorrigerEntree, "corriger une entrée de stock", ct);
        var motif = d.Motif?.Trim();
        if (string.IsNullOrEmpty(motif)) throw new RegleMetierException("Indiquez le motif de la correction.", "motif");
        if (motif.Length > MotifMax) throw new RegleMetierException($"Motif trop long ({MotifMax} caractères au plus).", "motif");
        var lot = await entrees.LotAsync(lotId, ct) ?? throw new RegleMetierException("Entrée de stock introuvable.");

        var champs = new List<ChampCorrige>();
        decimal? ppa = null, shp = null;
        string? numero = null;
        DateOnly? peremption = null;
        int? quantite = null;
        var bloquer = false;

        if (d.Ppa is { } p && p != lot.Ppa)
        {
            if (p < 0) throw new RegleMetierException("Le PPA ne peut pas être négatif.", "ppa");
            ppa = p;
            champs.Add(new("ppa", Montant(lot.Ppa), Montant(p)));
        }
        if (d.NumeroLot is not null)
        {
            var n = RegleLot.Normaliser(d.NumeroLot);
            if (n.Length == 0) throw new RegleMetierException("Indiquez le nouveau n° de lot.", "numero_lot");
            if (n != lot.NumeroLot)
            {
                numero = n;
                champs.Add(new("numero_lot", lot.NumeroLot, n));
                // Même règle qu'à la réception : un n° de lot rappelé (autre lot bloqué du même produit) n'est jamais vendable sans choix.
                // La correction ne le laisse pas vendable : le lot corrigé est bloqué (le blocage figure au journal des actions sensibles).
                if (!lot.Bloque && await entrees.NumeroRappeleAsync(lot.LotId, lot.ProduitId, n, ct))
                    bloquer = true;
            }
        }
        if (d.DatePeremption is { } dp && dp != lot.DatePeremption)
        {
            peremption = dp;
            champs.Add(new("date_peremption", Date(lot.DatePeremption), Date(dp)));
        }
        if (d.Shp is { } s && s != lot.Shp)
        {
            if (s < 0) throw new RegleMetierException("Le SHP ne peut pas être négatif.", "shp");
            shp = s;
            champs.Add(new("shp", Montant(lot.Shp), Montant(s)));
        }
        if (d.QuantiteRestante is { } q && q != lot.Quantite)
        {
            if (q < 0) throw new RegleMetierException("La quantité restante ne peut pas être négative.", "quantite");
            if (q < lot.BoitesEnStock)
                throw new RegleMetierException(
                    $"{lot.BoitesEnStock} boîte(s) étiquetée(s) de ce lot sont en stock : retirez d’abord les boîtes absentes (Sortie de stock ou inventaire).", "quantite");
            quantite = q;
            champs.Add(new("quantite", lot.Quantite.ToString(CultureInfo.InvariantCulture), q.ToString(CultureInfo.InvariantCulture)));
        }
        if (champs.Count == 0) throw new RegleMetierException("Aucun changement à enregistrer.");

        // Contrôle préalable de l'unicité (produit, grossiste, n° de lot, péremption) : message clair au lieu d'un refus de la base
        if ((numero is not null || peremption is not null)
            && await entrees.LotIdentiqueExisteAsync(lot.LotId, lot.ProduitId, lot.FournisseurId, numero ?? lot.NumeroLot, peremption ?? lot.DatePeremption, ct))
            throw new RegleMetierException(
                $"{lot.Produit} : un autre lot {numero ?? lot.NumeroLot} du {Date(peremption ?? lot.DatePeremption)} existe déjà chez {lot.Fournisseur}. "
                + "Corrigez plutôt cet autre lot, ou vérifiez le n° et la date.", "numero_lot");

        // Journal des actions sensibles écrit dans la transaction de la correction
        var journal = new JournalDansTransaction("correction_entree_stock", "lot", lot.LotId,
            new Dictionary<string, object?>
            {
                ["produit"] = lot.Produit, ["lot"] = lot.NumeroLot, ["motif"] = motif, ["lot_bloque_numero_rappele"] = bloquer,
                ["champs"] = champs.Select(c => $"{c.Champ} : {c.AncienneValeur ?? "—"} → {c.NouvelleValeur ?? "—"}").ToList()
            }, Permissions.StockCorrigerEntree);
        await entrees.CorrigerAsync(new CorrectionAEcrire(lot.LotId, lot.Quantite, ppa, numero, peremption, shp, quantite, motif, utilisateurId, champs,
            bloquer, journal), ct);
        return await entrees.LotAsync(lotId, ct) ?? lot;
    }

    private static string? Montant(decimal? m) => m?.ToString("0.00", CultureInfo.InvariantCulture);
    private static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
