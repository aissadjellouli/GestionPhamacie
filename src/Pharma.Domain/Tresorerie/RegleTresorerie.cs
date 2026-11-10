using Pharma.Domain.Commun;

namespace Pharma.Domain.Tresorerie;

/// <summary>Mouvements saisis à la main dans la trésorerie (les autres sont calculés : cartes, chèques remis, grossistes).</summary>
public enum NatureMouvement { Depense, Encaissement, VersementOrganisme }

public static class RegleTresorerie
{
    /// <summary>Catégories de dépenses (code, libellé).</summary>
    public static readonly IReadOnlyList<(string Code, string Libelle)> Categories =
    [
        ("loyer", "Loyer et charges"), ("energie", "Électricité, eau, gaz"), ("salaires", "Salaires et cotisations"),
        ("impots", "Impôts et taxes"), ("fournitures", "Fournitures et consommables"), ("entretien", "Entretien et réparations"),
        ("autre", "Autre dépense")
    ];

    public static NatureMouvement Nature(string? code) => code switch
    {
        "depense" => NatureMouvement.Depense,
        "encaissement" => NatureMouvement.Encaissement,
        "versement_organisme" => NatureMouvement.VersementOrganisme,
        _ => throw new RegleMetierException("Type de mouvement inconnu : dépense, encaissement ou versement d’un organisme.")
    };

    public static string Code(this NatureMouvement n) => n switch
    {
        NatureMouvement.Depense => "depense",
        NatureMouvement.Encaissement => "encaissement",
        _ => "versement_organisme"
    };

    /// <summary>Montant positif, libellé, date ni future ni antérieure au départ du compte ; catégorie (dépense) ou organisme (versement).</summary>
    public static void VerifierMouvement(NatureMouvement nature, decimal montant, string? libelle, string? categorie, long? organismeId,
        DateOnly date, DateOnly aujourdhui, DateOnly departCompte)
    {
        VerifierMontantEtDate(montant, date, aujourdhui, departCompte);
        if (nature == NatureMouvement.Depense && !Categories.Any(c => c.Code == categorie))
            throw new RegleMetierException("Choisissez la catégorie de la dépense.");
        if (nature == NatureMouvement.VersementOrganisme && organismeId is null)
            throw new RegleMetierException("Choisissez l’organisme qui a versé (CNAS, CASNOS…).");
        if (nature != NatureMouvement.VersementOrganisme && string.IsNullOrWhiteSpace(libelle))
            throw new RegleMetierException("Indiquez le libellé du mouvement.");
    }

    public static void VerifierMontantEtDate(decimal montant, DateOnly date, DateOnly aujourdhui, DateOnly departCompte)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant doit être supérieur à 0.");
        if (decimal.Round(montant, 2) != montant) throw new RegleMetierException("Le montant compte au plus deux décimales.");
        if (date > aujourdhui) throw new RegleMetierException("La date ne peut pas être dans le futur.");
        if (date < departCompte)
            throw new RegleMetierException($"La date précède le départ du compte ({departCompte:dd/MM/yyyy}) : elle ne compterait pas dans son solde.");
    }

    /// <summary>Un transfert relie deux comptes différents, tous deux actifs.</summary>
    public static void VerifierTransfert(long de, long vers)
    {
        if (de == vers) throw new RegleMetierException("Choisissez deux comptes différents.");
    }

    /// <summary>Argent versé au coffre depuis une caisse clôturée : au plus ce qui a été compté.</summary>
    public static void VerifierVersementCaisse(decimal montant, decimal compte)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant versé doit être supérieur à 0.");
        if (montant > compte) throw new RegleMetierException($"Le versement dépasse l’argent compté à la clôture ({compte:N2} DA).");
    }

    /// <summary>Automatique : compté moins le fond par défaut (laissé au tiroir pour le lendemain), jamais négatif.</summary>
    public static decimal VersementAutomatique(decimal compte, decimal fondDefaut) => Math.Max(compte - Math.Max(fondDefaut, 0), 0);
}
