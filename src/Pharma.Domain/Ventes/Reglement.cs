using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

public enum ModePaiement
{
    Especes,
    Carte,
    Cheque,
    Credit
}

/// <summary>
/// Règlement de la part du patient, en un seul mode de paiement, éventuellement précédé d'une part payée par l'avoir du patient
/// (script 106) : Montant est alors ce qui reste à payer dans le mode choisi.
/// </summary>
public sealed record Reglement(ModePaiement Mode, decimal Montant, decimal? MontantRecu, decimal Avoir = 0)
{
    /// <summary>Monnaie à rendre (espèces seulement).</summary>
    public decimal Rendu => Mode == ModePaiement.Especes && MontantRecu is { } recu ? recu - Montant : 0;

    public static Reglement Pour(Vente vente, ModePaiement mode, decimal? montantRecu, decimal avoir = 0)
    {
        if (avoir > 0)
        {
            if (mode == ModePaiement.Credit) throw new RegleMetierException("L’avoir ne se combine pas avec le crédit : utilisez-le avec espèces, carte ou chèque.", "avoir");
            if (avoir > vente.PartPatient) throw new RegleMetierException($"L’avoir utilisé dépasse le montant à payer ({vente.PartPatient:N2} DA).", "avoir");
            var reste = vente.PartPatient - avoir;
            if (mode != ModePaiement.Especes) return new Reglement(mode, reste, null, avoir);
            return new Reglement(mode, reste, RecuVerifie(montantRecu, reste), avoir);
        }
        var du = vente.PartPatient;
        if (mode != ModePaiement.Especes)
            return new Reglement(mode, du, null);
        return new Reglement(mode, du, RecuVerifie(montantRecu, du));
    }

    /// <summary>
    /// Espèces (2026-11-22, décision du titulaire) : dès qu'il reste à payer, le montant reçu est obligatoire et doit couvrir ce
    /// qui reste à payer, comme à l'écran de la caisse ; rien à payer (tiers payant total, avoir ou don total) : appoint exact.
    /// </summary>
    private static decimal RecuVerifie(decimal? montantRecu, decimal aPayer)
    {
        if (montantRecu is not { } recu)
        {
            if (aPayer > 0) throw new RegleMetierException("Saisissez le montant reçu en espèces.", "montant_recu");
            return aPayer;
        }
        if (recu < aPayer)
            throw new RegleMetierException($"Montant reçu insuffisant : {recu:N2} DA pour {aPayer:N2} DA à payer.", "montant_recu");
        return recu;
    }
}
