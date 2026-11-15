using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>Remboursement d'un crédit client au comptoir : au plus ce que le patient doit.</summary>
public static class RegleReglementCredit
{
    public static void Verifier(decimal montant, decimal soldeCredit, string? mode)
    {
        if (mode is not ("especes" or "carte" or "cheque")) throw new RegleMetierException("Mode de règlement : espèces, carte ou chèque.");
        if (montant <= 0) throw new RegleMetierException("Le montant réglé doit être supérieur à 0.", "montant");
        if (soldeCredit <= 0) throw new RegleMetierException("Ce patient n’a pas de crédit à rembourser.");
        if (montant > soldeCredit) throw new RegleMetierException($"Le patient ne doit que {soldeCredit:N2} DA.", "montant");
    }

    /// <summary>
    /// Ventes cochées par défaut (2026-11-15) : les plus anciennes (restes donnés dans cet ordre) jusqu'à couvrir le montant réglé.
    /// Le vendeur peut ensuite changer le choix du patient.
    /// </summary>
    public static IReadOnlyList<long> VentesParDefaut(decimal montant, IEnumerable<(long VenteId, decimal Reste)> restesDuPlusAncien)
    {
        var choisies = new List<long>();
        var couvert = 0m;
        foreach (var (vente, reste) in restesDuPlusAncien)
        {
            if (couvert >= montant) break;
            if (reste <= 0) continue;
            choisies.Add(vente);
            couvert += reste;
        }
        return choisies;
    }

    /// <summary>
    /// Surplus d'un encaissement de dette (2026-11-15) : part du montant qui dépasse le reste des ventes cochées, et la dette qu'il soldera
    /// (le serveur impute ce surplus au plus ancien) : la dette reprise de l'ancien logiciel s'il en reste (VenteId null), sinon la plus
    /// ancienne vente non cochée avec un reste. Aucune vente cochée : pas de surplus (tout le montant solde le plus ancien, comme avant).
    /// </summary>
    public static (decimal Surplus, bool Reprise, long? VenteId) Surplus(decimal montant, IReadOnlyCollection<long> cochees,
        IEnumerable<(long VenteId, decimal Reste)> restesDuPlusAncien, decimal detteRepriseRestante)
    {
        if (cochees.Count == 0 || montant <= 0) return (0, false, null);
        var ventes = restesDuPlusAncien.Where(v => v.Reste > 0).ToList();
        var surplus = montant - ventes.Where(v => cochees.Contains(v.VenteId)).Sum(v => v.Reste);
        if (surplus <= 0) return (0, false, null);
        if (detteRepriseRestante > 0) return (surplus, true, null);
        var cible = ventes.FirstOrDefault(v => !cochees.Contains(v.VenteId));
        return (surplus, false, cible.Reste > 0 ? cible.VenteId : null);
    }

    /// <summary>
    /// Imputation d'un règlement sur les ventes choisies (087, 2026-11-15), dans l'ordre donné, au plus le reste de chacune.
    /// Le surplus (ventes cochées qui ne couvrent pas le montant) n'est rattaché à aucune vente : il solde la dette la plus ancienne.
    /// </summary>
    public static IReadOnlyList<(long VenteId, decimal Montant)> Imputer(decimal montant, IEnumerable<(long VenteId, decimal Reste)> choisies)
    {
        var parts = new List<(long, decimal)>();
        var disponible = Math.Max(montant, 0);
        foreach (var (vente, reste) in choisies)
        {
            if (disponible <= 0) break;
            if (reste <= 0) continue;
            var part = Math.Min(reste, disponible);
            parts.Add((vente, part));
            disponible -= part;
        }
        return parts;
    }
}

/// <summary>Vente à crédit vue par l'imputation : montant (net des retours) et ce qui lui a été imputé directement.</summary>
public sealed record VenteImputee(long VenteId, decimal Montant, decimal Impute);

/// <summary>
/// Réglé et reste de chaque vente à crédit d'un patient : même calcul que la vue v_vente_credit (087_imputation_credit.sql).
/// Les règlements rattachés à une vente (credit_imputation) sont déduits de cette vente ; le reliquat non rattaché
/// (ouverture + ventes − dette − imputations) solde la dette d'ouverture puis les ventes des plus anciennes aux plus récentes.
/// </summary>
public static class RegleImputationCredit
{
    public static IReadOnlyList<(long VenteId, decimal Regle, decimal Reste)> Calculer(decimal ouverture, decimal dette, IReadOnlyList<VenteImputee> ventesDuPlusAncien)
    {
        var ventes = ventesDuPlusAncien.Where(v => v.Montant > 0).ToList();
        var imputes = ventes.Select(v => Math.Min(v.Impute, v.Montant)).ToList();
        var libre = Math.Max(ouverture + ventes.Sum(v => v.Montant) - dette - imputes.Sum(), 0);
        var resultat = new List<(long, decimal, decimal)>();
        var cumul = ouverture;
        for (var i = 0; i < ventes.Count; i++)
        {
            var capacite = ventes[i].Montant - imputes[i];
            var part = Math.Min(Math.Max(libre - cumul, 0), capacite);
            cumul += capacite;
            var regle = Math.Min(imputes[i] + part, ventes[i].Montant);
            resultat.Add((ventes[i].VenteId, regle, ventes[i].Montant - regle));
        }
        return resultat;
    }
}
