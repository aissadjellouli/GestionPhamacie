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
            var r = montantRecu ?? reste;
            if (r < reste) throw new RegleMetierException($"Montant reçu insuffisant : {r:N2} DA pour {reste:N2} DA à payer.");
            return new Reglement(mode, reste, r, avoir);
        }
        var du = vente.PartPatient;
        if (mode != ModePaiement.Especes)
            return new Reglement(mode, du, null);

        var recu = montantRecu ?? du;   // non saisi : l'appoint exact
        if (recu < du)
            throw new RegleMetierException($"Montant reçu insuffisant : {recu:N2} DA pour {du:N2} DA à payer.");
        return new Reglement(mode, du, recu);
    }
}
