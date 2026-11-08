using Pharma.Domain.Achats;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;

namespace Pharma.Domain.Stock;

public class Lot
{
    public long Id { get; init; }
    public long ProduitId { get; init; }
    public Produit? Produit { get; init; }
    public long ReceptionId { get; init; }
    public long FournisseurId { get; init; }
    public Fournisseur? Fournisseur { get; init; }
    public string NumeroLot { get; init; } = "";
    public DateOnly DatePeremption { get; init; }
    public int QuantiteDisponible { get; init; }
    public decimal PrixAchatHt { get; init; }
    public bool Bloque { get; init; }
    public string? MotifBlocage { get; init; }
    public bool DateAVerifier { get; init; }

    public bool EstPerime(DateOnly aujourdhui) => DatePeremption <= aujourdhui;

    public bool EstVendable(DateOnly aujourdhui) => RaisonNonVendable(aujourdhui) is null;

    /// <summary>Pourquoi ce lot ne peut pas être vendu (null s'il est vendable).</summary>
    public Stock.RaisonNonVendable? RaisonNonVendable(DateOnly aujourdhui) =>
        Bloque ? Stock.RaisonNonVendable.Bloque
        : EstPerime(aujourdhui) ? Stock.RaisonNonVendable.Perime
        : QuantiteDisponible <= 0 ? Stock.RaisonNonVendable.Epuise
        : null;

    /// <summary>Même règle que le déclencheur en base : refus clair avant d'envoyer la vente.</summary>
    public void VerifierVendable(DateOnly aujourdhui)
    {
        if (Bloque)
            throw new RegleMetierException($"Lot {NumeroLot} bloqué : {MotifBlocage ?? "sans motif"}.");
        if (EstPerime(aujourdhui))
            throw new RegleMetierException($"Lot {NumeroLot} périmé depuis le {DatePeremption:dd/MM/yyyy}.");
        if (QuantiteDisponible <= 0)
            throw new RegleMetierException($"Lot {NumeroLot} épuisé.");
    }

    /// <summary>Lot à servir en premier : celui qui expire le plus tôt (FEFO).</summary>
    public static Lot? Fefo(IEnumerable<Lot> lots, DateOnly aujourdhui) =>
        lots.Where(l => l.EstVendable(aujourdhui))
            .OrderBy(l => l.DatePeremption).ThenBy(l => l.Id)
            .FirstOrDefault();
}
