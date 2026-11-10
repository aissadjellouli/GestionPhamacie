using Pharma.Domain.Commun;

namespace Pharma.Domain.Achats;

public enum MoyenReglement { Especes, Cheque, Virement, Effet }

/// <summary>Facture d'un grossiste encore à payer (réception), et part qui lui est imputée.</summary>
public sealed record FactureAPayer(long ReceptionId, DateTime Date, decimal Reste);

/// <summary>
/// Règlement d'un grossiste, imputé facture par facture. Espèces : un coffre à part, avec un complément éventuel pris dans
/// le tiroir de la caisse. Chèque, virement, effet : référence obligatoire ; effet : date d'échéance.
/// </summary>
public static class RegleReglement
{
    public static MoyenReglement Moyen(string? code) => code switch
    {
        "especes" => MoyenReglement.Especes,
        "cheque" => MoyenReglement.Cheque,
        "virement" => MoyenReglement.Virement,
        "effet" => MoyenReglement.Effet,
        _ => throw new RegleMetierException("Moyen de paiement inconnu : espèces, chèque, virement ou effet.")
    };

    public static string Code(this MoyenReglement m) => m switch
    {
        MoyenReglement.Especes => "especes",
        MoyenReglement.Cheque => "cheque",
        MoyenReglement.Virement => "virement",
        _ => "effet"
    };

    public static void Verifier(decimal montant, MoyenReglement moyen, string? reference, DateOnly? echeance, decimal partTiroir, DateOnly dateReglement, DateOnly aujourdhui)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant réglé doit être supérieur à 0.");
        if (dateReglement > aujourdhui) throw new RegleMetierException("La date du règlement ne peut pas être dans le futur.");
        if (moyen != MoyenReglement.Especes && string.IsNullOrWhiteSpace(reference))
            throw new RegleMetierException(moyen switch
            {
                MoyenReglement.Cheque => "Indiquez le n° du chèque.",
                MoyenReglement.Virement => "Indiquez la référence du virement.",
                _ => "Indiquez le n° de l’effet."
            });
        if (moyen == MoyenReglement.Effet && echeance is null) throw new RegleMetierException("Indiquez la date d’échéance de l’effet.");
        if (partTiroir < 0) throw new RegleMetierException("Le complément pris dans le tiroir ne peut pas être négatif.");
        if (partTiroir > 0 && moyen != MoyenReglement.Especes) throw new RegleMetierException("Seul un paiement en espèces peut être complété par le tiroir de la caisse.");
        if (partTiroir > montant) throw new RegleMetierException("Le complément pris dans le tiroir dépasse le montant réglé.");
    }

    /// <summary>Montant réparti sur les factures, des plus anciennes aux plus récentes ; le surplus reste « non imputé ».</summary>
    public static IReadOnlyList<(long ReceptionId, decimal Montant)> ImputerAuxPlusAnciennes(decimal montant, IEnumerable<FactureAPayer> factures)
    {
        var reste = montant;
        var imputations = new List<(long, decimal)>();
        foreach (var f in factures.Where(f => f.Reste > 0).OrderBy(f => f.Date).ThenBy(f => f.ReceptionId))
        {
            if (reste <= 0) break;
            var part = Math.Min(reste, f.Reste);
            imputations.Add((f.ReceptionId, part));
            reste -= part;
        }
        return imputations;
    }

    /// <summary>Imputations choisies : chacune au plus le reste de sa facture, et leur total au plus le montant réglé.</summary>
    public static void VerifierImputations(decimal montant, IReadOnlyList<(long ReceptionId, decimal Montant)> imputations, IReadOnlyDictionary<long, decimal> restes)
    {
        foreach (var (id, m) in imputations)
        {
            if (m <= 0) throw new RegleMetierException("Montant imputé à une facture invalide.");
            if (!restes.TryGetValue(id, out var r)) throw new RegleMetierException("Facture inconnue pour ce grossiste.");
            if (m > r) throw new RegleMetierException($"Une facture n’a plus que {r:N2} DA à payer.");
        }
        if (imputations.Sum(i => i.Montant) > montant) throw new RegleMetierException("Le total imputé aux factures dépasse le montant réglé.");
    }
}

