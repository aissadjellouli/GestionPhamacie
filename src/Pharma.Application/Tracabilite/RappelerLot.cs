using Pharma.Domain.Commun;

namespace Pharma.Application.Tracabilite;

/// <summary>Une vente du lot rappelé : patient à contacter, ou vente anonyme.</summary>
public sealed record VenteARappeler(string Patient, string? Telephone, DateTime DateVente, string Ticket, int Boites, bool Anonyme);

/// <summary>
/// Rappel d'un lot, ou état d'un lot consulté.
/// LotsBloques : au rappel, lots bloqués par ce rappel (0 : tout était déjà bloqué, rien n'a été fait) ; à la consultation,
/// lots de ce numéro déjà bloqués. LotsNonBloques : lots de ce numéro encore vendables (consultation).
/// RappeleLe, Motif : premier rappel inscrit au journal (consultation) ; à défaut, motif du blocage du lot.
/// </summary>
public sealed record ResultatRappel(
    string Produit, string NumeroLot, int LotsBloques, int BoitesEnStock,
    IReadOnlyList<VenteARappeler> Ventes, string? Emplacements = null,
    int LotsNonBloques = 0, DateTime? RappeleLe = null, string? Motif = null)
{
    public int BoitesVendues => Ventes.Sum(v => v.Boites);
    public int VentesAnonymes => Ventes.Count(v => v.Anonyme);
    public int PatientsAContacter => Ventes.Where(v => !v.Anonyme).Select(v => (v.Patient, v.Telephone)).Distinct().Count();
    /// <summary>Lot bloqué chez tous les grossistes : rien à rebloquer.</summary>
    public bool ToutBloque => LotsBloques > 0 && LotsNonBloques == 0;
}

public interface IRappelLot
{
    /// <summary>
    /// Bloque le lot chez les grossistes où il est encore vendable, trace chaque boîte en stock de ces lots et inscrit l'action
    /// au journal, en une transaction. Lot déjà bloqué partout : rien n'est écrit, LotsBloques vaut 0.
    /// Null si aucun lot ne porte ce numéro pour ce produit.
    /// </summary>
    Task<ResultatRappel?> RappelerAsync(long produitId, string numeroLot, string motif, long utilisateurId, CancellationToken ct = default);

    /// <summary>État du lot et ventes concernées, sans rien modifier. Null si aucun lot ne porte ce numéro pour ce produit.</summary>
    Task<ResultatRappel?> ConsulterAsync(long produitId, string numeroLot, CancellationToken ct = default);
}

/// <summary>Rappel de lot (fonction SQL rappel_lot) : blocage immédiat et liste des patients à contacter.</summary>
public sealed class RappelerLot(IRappelLot rappel, Notifications.IAlertesTitulaire? alertes = null)
{
    public async Task<ResultatRappel> ExecuterAsync(
        long produitId, string? numeroLot, string? motif, long utilisateurId, CancellationToken ct = default)
    {
        var lot = numeroLot?.Trim();
        var raison = motif?.Trim();
        if (produitId <= 0) throw new RegleMetierException("Choisissez le produit concerné par le rappel.", "produit_rappel");
        if (string.IsNullOrEmpty(lot)) throw new RegleMetierException("Indiquez le numéro de lot rappelé.", "lot_rappel");
        if (string.IsNullOrEmpty(raison)) throw new RegleMetierException("Indiquez le motif du rappel (il est inscrit au journal).", "motif_rappel");

        var r = await rappel.RappelerAsync(produitId, lot, raison, utilisateurId, ct)
                ?? throw new RegleMetierException($"Aucun lot {lot} pour ce produit : vérifiez le numéro.");
        // Déjà bloqué partout : rien n'a été modifié, ni journal ni alerte
        if (r.LotsBloques == 0)
            throw new RegleMetierException($"Lot {r.NumeroLot} déjà rappelé : il est bloqué chez tous les grossistes, rien n'a été modifié. " +
                                           "Consultez la liste des patients concernés.", "lot_rappel");
        // Alerte du titulaire : le blocage est fait, la notification ne doit pas le faire échouer
        if (alertes is not null)
        {
            try { await alertes.LotBloqueAsync(r.Produit, r.NumeroLot, r.BoitesEnStock, r.BoitesVendues, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return r;
    }
}

/// <summary>Consultation d'un lot (déjà rappelé ou non) : état du blocage et patients concernés, sans rien bloquer.</summary>
public sealed class ConsulterRappelLot(IRappelLot rappel)
{
    public async Task<ResultatRappel> ExecuterAsync(long produitId, string? numeroLot, CancellationToken ct = default)
    {
        var lot = numeroLot?.Trim();
        if (produitId <= 0) throw new RegleMetierException("Choisissez le produit concerné par le rappel.", "produit_rappel");
        if (string.IsNullOrEmpty(lot)) throw new RegleMetierException("Indiquez le numéro de lot rappelé.", "lot_rappel");
        return await rappel.ConsulterAsync(produitId, lot, ct)
               ?? throw new RegleMetierException($"Aucun lot {lot} pour ce produit : vérifiez le numéro.", "lot_rappel");
    }
}
