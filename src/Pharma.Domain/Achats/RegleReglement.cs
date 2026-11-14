using Pharma.Domain.Commun;

namespace Pharma.Domain.Achats;

/// <summary>Effet : la traite (code « effet » en base, « traite » à l'écran). Prelevement : prélèvement bancaire.</summary>
public enum MoyenReglement { Especes, Cheque, Virement, Effet, Prelevement }

/// <summary>Facture d'un grossiste encore à payer (réception), et part qui lui est imputée.</summary>
public sealed record FactureAPayer(long ReceptionId, DateTime Date, decimal Reste);

/// <summary>
/// Règlement d'un grossiste, imputé facture par facture. Espèces : le coffre, avec un complément éventuel pris dans le tiroir de
/// la caisse. Chèque, virement, prélèvement : référence et banque débitée obligatoires. Traite : n° et
/// date d'échéance obligatoires, banque facultative (sans banque, la traite est payée à l'échéance sur confirmation du titulaire).
/// </summary>
public static class RegleReglement
{
    public static MoyenReglement Moyen(string? code) => code switch
    {
        "especes" => MoyenReglement.Especes,
        "cheque" => MoyenReglement.Cheque,
        "virement" => MoyenReglement.Virement,
        "effet" => MoyenReglement.Effet,
        "prelevement" => MoyenReglement.Prelevement,
        _ => throw new RegleMetierException("Moyen de paiement inconnu : espèces, chèque, virement, prélèvement ou traite.")
    };

    public static string Code(this MoyenReglement m) => m switch
    {
        MoyenReglement.Especes => "especes",
        MoyenReglement.Cheque => "cheque",
        MoyenReglement.Virement => "virement",
        MoyenReglement.Prelevement => "prelevement",
        _ => "effet"
    };

    /// <summary>Libellé lu par l'employé (« traite » pour le code « effet »).</summary>
    public static string Libelle(this MoyenReglement m) => m switch
    {
        MoyenReglement.Especes => "espèces",
        MoyenReglement.Cheque => "chèque",
        MoyenReglement.Virement => "virement",
        MoyenReglement.Prelevement => "prélèvement",
        _ => "traite"
    };

    /// <summary>Chèque, virement et prélèvement sont débités d'une banque de la trésorerie, choisie au règlement.</summary>
    public static bool BanqueObligatoire(this MoyenReglement m) => m is MoyenReglement.Cheque or MoyenReglement.Virement or MoyenReglement.Prelevement;

    /// <summary>Banque débitée : obligatoire pour chèque, virement et prélèvement ; facultative pour une traite ; aucune en espèces.</summary>
    public static void VerifierBanque(MoyenReglement moyen, long? compteId)
    {
        if (moyen.BanqueObligatoire() && compteId is not > 0)
            throw new RegleMetierException($"Choisissez la banque débitée par le {moyen.Libelle()}.", "compte");
    }

    /// <summary>
    /// Paiement d'une traite à son échéance (confirmé par le titulaire) : espèces, chèque, virement ou prélèvement ; banque obligatoire
    /// hors espèces, avec sa référence (n° du chèque, référence du virement ou du prélèvement) ; complément du tiroir seulement en
    /// espèces, au centime, au plus le montant ; date ni future ni avant la remise.
    /// </summary>
    public static MoyenReglement VerifierPaiementTraite(string? moyen, long? compteId, string? reference, decimal partTiroir, decimal montant,
        DateOnly date, DateOnly dateRemise, DateOnly aujourdhui)
    {
        var m = moyen switch
        {
            "especes" => MoyenReglement.Especes,
            "cheque" => MoyenReglement.Cheque,
            "virement" => MoyenReglement.Virement,
            "prelevement" => MoyenReglement.Prelevement,
            _ => throw new RegleMetierException("Une traite se paie en espèces, par chèque, par virement ou par prélèvement.", "moyen")
        };
        VerifierBanque(m, compteId);
        if (m != MoyenReglement.Especes && string.IsNullOrWhiteSpace(reference)) throw new RegleMetierException(MessageReference(m));
        if (date > aujourdhui) throw new RegleMetierException("La date du paiement ne peut pas être dans le futur.", "date");
        if (date < dateRemise) throw new RegleMetierException("La date du paiement précède la remise de la traite.", "date");
        if (partTiroir < 0 || !AuCentime(partTiroir))
            throw new RegleMetierException("Le complément pris dans le tiroir se saisit au centime près, positif.", "tiroir");
        if (partTiroir > 0 && m != MoyenReglement.Especes)
            throw new RegleMetierException("Seul un paiement en espèces peut être complété par le tiroir de la caisse.", "tiroir");
        if (partTiroir > montant) throw new RegleMetierException("Le complément pris dans le tiroir dépasse le montant de la traite.", "tiroir");
        return m;
    }

    /// <summary>Référence obligatoire hors espèces : n° du chèque ou de la traite, référence du virement ou du prélèvement.</summary>
    private static string MessageReference(MoyenReglement moyen) => moyen switch
    {
        MoyenReglement.Cheque => "Indiquez le n° du chèque.",
        MoyenReglement.Virement => "Indiquez la référence du virement.",
        MoyenReglement.Prelevement => "Indiquez la référence du prélèvement.",
        _ => "Indiquez le n° de la traite."
    };

    public static void Verifier(decimal montant, MoyenReglement moyen, string? reference, DateOnly? echeance, decimal partTiroir, DateOnly dateReglement, DateOnly aujourdhui)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant réglé doit être supérieur à 0.", "montant");
        if (!AuCentime(montant)) throw new RegleMetierException("Le montant réglé se saisit au centime près (2 décimales au plus).", "montant");
        if (!AuCentime(partTiroir)) throw new RegleMetierException("Le complément pris dans le tiroir se saisit au centime près (2 décimales au plus).", "tiroir");
        if (dateReglement > aujourdhui) throw new RegleMetierException("La date du règlement ne peut pas être dans le futur.", "date");
        if (moyen != MoyenReglement.Especes && string.IsNullOrWhiteSpace(reference))
            throw new RegleMetierException(MessageReference(moyen));
        if (moyen == MoyenReglement.Effet && echeance is null) throw new RegleMetierException("Indiquez la date d’échéance de la traite.", "echeance");
        if (partTiroir < 0) throw new RegleMetierException("Le complément pris dans le tiroir ne peut pas être négatif.", "tiroir");
        if (partTiroir > 0 && moyen != MoyenReglement.Especes) throw new RegleMetierException("Seul un paiement en espèces peut être complété par le tiroir de la caisse.");
        if (partTiroir > montant) throw new RegleMetierException("Le complément pris dans le tiroir dépasse le montant réglé.", "tiroir");
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
            if (!AuCentime(m)) throw new RegleMetierException("Le montant imputé à une facture se saisit au centime près (2 décimales au plus).");
            if (!restes.TryGetValue(id, out var r)) throw new RegleMetierException("Facture inconnue pour ce grossiste.");
            if (m > r) throw new RegleMetierException($"Une facture n’a plus que {r:N2} DA à payer.");
        }
        if (imputations.Sum(i => i.Montant) > montant) throw new RegleMetierException("Le total imputé aux factures dépasse le montant réglé.", "montant");
    }

    /// <summary>Montant en dinars au centime près : 2 décimales au plus (la base les arrondirait sinon sans le dire).</summary>
    public static bool AuCentime(decimal montant) => Arrondi.AuCentime(montant);
}

