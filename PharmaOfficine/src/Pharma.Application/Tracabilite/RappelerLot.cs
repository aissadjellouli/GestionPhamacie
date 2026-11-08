using Pharma.Domain.Commun;

namespace Pharma.Application.Tracabilite;

/// <summary>Une vente du lot rappelé : patient à contacter, ou vente anonyme.</summary>
public sealed record VenteARappeler(string Patient, string? Telephone, DateTime DateVente, string Ticket, int Boites, bool Anonyme);

public sealed record ResultatRappel(
    string Produit, string NumeroLot, int LotsBloques, int BoitesEnStock,
    IReadOnlyList<VenteARappeler> Ventes)
{
    public int BoitesVendues => Ventes.Sum(v => v.Boites);
    public int VentesAnonymes => Ventes.Count(v => v.Anonyme);
    public int PatientsAContacter => Ventes.Where(v => !v.Anonyme).Select(v => (v.Patient, v.Telephone)).Distinct().Count();
}

public interface IRappelLot
{
    /// <summary>
    /// Bloque le lot chez tous les grossistes, trace chaque boîte en stock et inscrit l'action au journal,
    /// en une transaction. Null si aucun lot ne porte ce numéro pour ce produit.
    /// </summary>
    Task<ResultatRappel?> RappelerAsync(long produitId, string numeroLot, string motif, long utilisateurId, CancellationToken ct = default);
}

/// <summary>Rappel de lot (fonction SQL rappel_lot) : blocage immédiat et liste des patients à contacter.</summary>
public sealed class RappelerLot(IRappelLot rappel)
{
    public async Task<ResultatRappel> ExecuterAsync(
        long produitId, string? numeroLot, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        var lot = numeroLot?.Trim();
        var raison = motif?.Trim();
        if (produitId <= 0) throw new RegleMetierException("Choisissez le produit concerné par le rappel.");
        if (string.IsNullOrEmpty(lot)) throw new RegleMetierException("Indiquez le numéro de lot rappelé.");
        if (string.IsNullOrEmpty(raison)) throw new RegleMetierException("Indiquez le motif du rappel (il est inscrit au journal).");

        return await rappel.RappelerAsync(produitId, lot, raison, utilisateurId, ct)
               ?? throw new RegleMetierException($"Aucun lot {lot} pour ce produit : vérifiez le numéro.");
    }
}
