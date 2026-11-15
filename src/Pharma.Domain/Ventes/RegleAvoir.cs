using Pharma.Domain.Commun;

namespace Pharma.Domain.Ventes;

/// <summary>
/// Avoir du patient (règles du titulaire, 2026-11-15, script 106) : argent que la pharmacie lui doit, séparé de sa dette (jamais une
/// dette négative). Il naît d'un trop-payé laissé en avoir à l'annulation d'une vente (vente à crédit en partie payée, régularisation
/// Chifa annulée) et sert à payer une vente ou une dette, ou se rend par le tiroir. Jamais négatif, sans expiration.
/// </summary>
public static class RegleAvoir
{
    public const string Mode = "avoir";

    public const string Credit = "credit";
    public const string Debit = "debit";

    public const string OrigineAnnulationVente = "annulation_vente";
    public const string OrigineTropPayeRegularisation = "trop_paye_regularisation";
    public const string OrigineSaisie = "saisie";
    public const string OriginePaiement = "paiement";
    public const string OrigineRemboursement = "remboursement";

    public static RegleMetierException Insuffisant(decimal solde) =>
        new($"Avoir insuffisant (solde : {Math.Max(solde, 0):N2} DA).", "avoir");

    /// <summary>
    /// Avoir utilisé en paiement : patient choisi, montant positif, au plus le solde et au plus ce qui est à payer. Le solde est revérifié
    /// sous verrou du patient au moment d'écrire (deux caisses ne consomment pas le même avoir).
    /// </summary>
    public static void VerifierUtilisation(decimal montant, decimal solde, decimal aPayer, bool patientConnu)
    {
        if (montant <= 0) return;
        if (!patientConnu) throw new RegleMetierException("Paiement par avoir : choisissez le patient.", "avoir");
        if (montant > aPayer) throw new RegleMetierException($"L’avoir utilisé dépasse le montant à payer ({aPayer:N2} DA).", "avoir");
        if (montant > solde) throw Insuffisant(solde);
    }

    /// <summary>Remboursement d'un avoir par le tiroir : montant positif, au plus le solde.</summary>
    public static void VerifierRemboursement(decimal montant, decimal solde)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant à rembourser doit être supérieur à 0.", "montant");
        if (montant > solde) throw Insuffisant(solde);
    }

    /// <summary>
    /// Annulation d'une vente payée en partie par avoir : l'avoir utilisé revient d'abord à l'avoir (jamais en espèces), le reste suit le
    /// mode de la vente. resteVente : part patient moins ce qui a déjà été rendu par des retours.
    /// </summary>
    public static (decimal AvoirRendu, decimal ResteAutreMode) AnnulationPayeeParAvoir(decimal resteVente, decimal avoirUtilise)
    {
        var reste = Math.Max(resteVente, 0);
        var avoir = Math.Min(Math.Max(avoirUtilise, 0), reste);
        return (avoir, reste - avoir);
    }

    public static string LibelleOrigine(string origine) => origine switch
    {
        OrigineAnnulationVente => "Annulation de vente",
        OrigineTropPayeRegularisation => "Trop-payé de régularisation",
        OrigineSaisie => "Saisie",
        OriginePaiement => "Utilisé en paiement",
        OrigineRemboursement => "Remboursé",
        _ => origine
    };
}
