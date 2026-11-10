using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

public enum ModePaiement
{
    Especes,
    Carte,
    Cheque,
    Credit
}

/// <summary>Règlement de la part du patient, en un seul mode de paiement.</summary>
public sealed record Reglement(ModePaiement Mode, decimal Montant, decimal? MontantRecu)
{
    /// <summary>Monnaie à rendre (espèces seulement).</summary>
    public decimal Rendu => Mode == ModePaiement.Especes && MontantRecu is { } recu ? recu - Montant : 0;

    public static Reglement Pour(Vente vente, ModePaiement mode, decimal? montantRecu)
    {
        var du = vente.PartPatient;
        if (mode != ModePaiement.Especes)
            return new Reglement(mode, du, null);

        var recu = montantRecu ?? du;   // non saisi : l'appoint exact
        if (recu < du)
            throw new RegleMetierException($"Montant reçu insuffisant : {recu:N2} DA pour {du:N2} DA à payer.");
        return new Reglement(mode, du, recu);
    }
}
