using Pharma.Domain.Commun;

namespace Pharma.Domain.Financements;

/// <summary>
/// Échéance d'un prêt : Montant = Capital + Interets + Tva + Marge + Assurance. Tva, Marge et Assurance facultatives (0) : la marge
/// est celle d'un financement islamique (mourabaha). CapitalRestant : capital restant dû tel que le tableau de la banque l'indique.
/// </summary>
public sealed record LigneEcheancier(DateOnly Date, decimal Capital, decimal Interets, decimal Tva, decimal Marge, decimal Montant, decimal CapitalRestant,
    decimal Assurance = 0)
{
    /// <summary>Part comptée en charge financière : intérêts, assurance, TVA et marge.</summary>
    public decimal Charges => Interets + Tva + Marge + Assurance;
}

/// <summary>
/// Coût d'un échéancier : totaux des intérêts, de l'assurance, de la TVA et de la marge ; CoutTotal (coût du crédit) = leur somme,
/// soit tout ce qui est payé en plus du capital.
/// </summary>
public sealed record CoutEcheancier(decimal Interets, decimal Assurance, decimal Tva, decimal Marge)
{
    public decimal CoutTotal => Interets + Assurance + Tva + Marge;

    public static CoutEcheancier De(IEnumerable<LigneEcheancier> lignes)
    {
        var l = lignes.ToList();
        return new(l.Sum(x => x.Interets), l.Sum(x => x.Assurance), l.Sum(x => x.Tva), l.Sum(x => x.Marge));
    }
}

/// <summary>
/// Conditions du contrat de prêt, saisies telles qu'au contrat et jamais recalculées : taux annuel en % (0 à 100, 4 décimales au
/// plus) et durée en mois (entière, 1 à 600). Facultatifs (prêts enregistrés avant ces champs).
/// </summary>
public static class RegleConditionsPret
{
    public const int DureeMax = 600;

    public static void Verifier(decimal? taux, int? dureeMois)
    {
        if (taux is { } t && (t < 0 || t > 100))
            throw new RegleMetierException("Le taux annuel se saisit en %, entre 0 et 100.", "taux");
        if (taux is { } d && decimal.Round(d, 4) != d)
            throw new RegleMetierException("Le taux annuel se saisit avec 4 décimales au plus.", "taux");
        if (dureeMois is { } m && (m < 1 || m > DureeMax))
            throw new RegleMetierException($"La durée se saisit en mois, de 1 à {DureeMax}.", "duree");
    }
}

/// <summary>Incohérence d'un échéancier. Ligne : n° de la ligne (à partir de 1), 0 pour l'échéancier entier. Champ : colonne en cause.</summary>
public sealed record AnomalieEcheancier(int Ligne, string Champ, string Message);

/// <summary>
/// Contrôles d'un échéancier avant son enregistrement (saisi, importé d'Excel ou de CSV, ou lu dans un PDF ou un scan) :
/// chaque ligne au centime, capital + intérêts + TVA + marge + assurance = échéance ; dates strictement croissantes, pas avant le prêt ;
/// capital restant dû jamais en hausse ; somme du capital (+ capital déjà remboursé par anticipation, script 170) = montant du prêt.
/// Tant qu'une incohérence reste, l'enregistrement est refusé.
/// </summary>
public static class RegleEcheancier
{
    public static IReadOnlyList<AnomalieEcheancier> Controler(decimal montantPret, DateOnly datePret, IReadOnlyList<LigneEcheancier> lignes,
        decimal capitalAnticipe = 0)
    {
        var anomalies = new List<AnomalieEcheancier>();
        if (lignes.Count == 0)
        {
            anomalies.Add(new(0, "lignes", "L’échéancier ne contient aucune échéance."));
            return anomalies;
        }
        for (var i = 0; i < lignes.Count; i++)
        {
            var l = lignes[i];
            var n = i + 1;
            foreach (var (champ, valeur) in new[] { ("capital", l.Capital), ("interets", l.Interets), ("tva", l.Tva), ("marge", l.Marge),
                         ("assurance", l.Assurance), ("montant", l.Montant), ("capital_restant", l.CapitalRestant) })
            {
                if (valeur < 0) anomalies.Add(new(n, champ, $"Ligne {n} : montant négatif ({Nom(champ)})."));
                else if (!Arrondi.AuCentime(valeur)) anomalies.Add(new(n, champ, $"Ligne {n} : {Nom(champ)} au centime près (2 décimales au plus)."));
            }
            if (l.Montant <= 0) anomalies.Add(new(n, "montant", $"Ligne {n} : l’échéance doit être supérieure à 0."));
            var somme = l.Capital + l.Interets + l.Tva + l.Marge + l.Assurance;
            if (somme != l.Montant)
                anomalies.Add(new(n, "montant", $"Ligne {n} : capital + intérêts + TVA + marge + assurance = {somme:N2} DA, l’échéance indique {l.Montant:N2} DA (écart {l.Montant - somme:N2} DA)."));
            if (i == 0 && l.Date < datePret)
                anomalies.Add(new(n, "date", $"Ligne {n} : l’échéance ({l.Date:dd/MM/yyyy}) précède la date du prêt ({datePret:dd/MM/yyyy})."));
            if (i > 0 && l.Date <= lignes[i - 1].Date)
                anomalies.Add(new(n, "date", $"Ligne {n} : la date ({l.Date:dd/MM/yyyy}) n’est pas après celle de la ligne {n - 1} ({lignes[i - 1].Date:dd/MM/yyyy})."));
            if (i > 0 && l.CapitalRestant > lignes[i - 1].CapitalRestant)
                anomalies.Add(new(n, "capital_restant", $"Ligne {n} : le capital restant dû augmente ({lignes[i - 1].CapitalRestant:N2} → {l.CapitalRestant:N2} DA)."));
            if (l.CapitalRestant > montantPret)
                anomalies.Add(new(n, "capital_restant", $"Ligne {n} : le capital restant dû dépasse le montant du prêt."));
        }
        var capital = lignes.Sum(l => l.Capital);
        if (capital + capitalAnticipe != montantPret)
            anomalies.Add(new(0, "capital", capitalAnticipe == 0
                ? $"La somme du capital ({capital:N2} DA) diffère du montant du prêt ({montantPret:N2} DA) : écart {montantPret - capital:N2} DA."
                : $"La somme du capital ({capital:N2} DA) et du capital remboursé par anticipation ({capitalAnticipe:N2} DA) diffère du montant du prêt ({montantPret:N2} DA) : écart {montantPret - capital - capitalAnticipe:N2} DA."));
        return anomalies;
    }

    /// <summary>Refuse l'échéancier tant qu'il reste une incohérence (message : la première, et le nombre des autres).</summary>
    public static void Exiger(decimal montantPret, DateOnly datePret, IReadOnlyList<LigneEcheancier> lignes, decimal capitalAnticipe = 0)
    {
        var anomalies = Controler(montantPret, datePret, lignes, capitalAnticipe);
        if (anomalies.Count == 0) return;
        var autres = anomalies.Count > 1 ? $" (et {anomalies.Count - 1} autre(s) incohérence(s))" : "";
        throw new RegleMetierException($"Échéancier incohérent : {anomalies[0].Message}{autres} Corrigez-le avant de l’enregistrer.", "echeancier");
    }

    /// <summary>Paiement d'une échéance : le capital diminue la dette envers la banque ; intérêts, assurance, TVA et marge sont des charges financières.</summary>
    public static (decimal Capital, decimal Charges) Parts(LigneEcheancier l) => (l.Capital, l.Charges);

    /// <summary>
    /// Paiement d'une échéance, en une ou plusieurs fois (décision du titulaire du 2026-11-26, script 170) : chaque paiement couvre d'abord
    /// les charges encore dues (intérêts, assurance, TVA, marge), puis le capital ; jamais plus que ce qui reste à payer sur l'échéance.
    /// </summary>
    public static (decimal Capital, decimal Charges) Imputer(LigneEcheancier l, decimal capitalDeja, decimal chargesDeja, decimal montant)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant payé doit être supérieur à 0.", "montant");
        if (!Arrondi.AuCentime(montant)) throw new RegleMetierException("Le montant payé se saisit au centime près (2 décimales au plus).", "montant");
        var reste = l.Montant - capitalDeja - chargesDeja;
        if (montant > reste)
            throw new RegleMetierException($"Le montant payé dépasse ce qui reste à payer sur cette échéance ({Math.Max(reste, 0):N2} DA).", "montant");
        var charges = Math.Min(montant, Math.Max(l.Charges - chargesDeja, 0));
        return (montant - charges, charges);
    }

    /// <summary>Capital restant dû après chaque échéance, calculé quand le tableau de la banque ne le donne pas.</summary>
    public static IReadOnlyList<decimal> Encours(decimal montantPret, IEnumerable<decimal> capitaux)
    {
        var reste = montantPret;
        var liste = new List<decimal>();
        foreach (var c in capitaux)
        {
            reste -= c;
            liste.Add(Math.Max(reste, 0));
        }
        return liste;
    }

    private static string Nom(string champ) => champ switch
    {
        "capital" => "capital", "interets" => "intérêts", "tva" => "TVA", "marge" => "marge", "assurance" => "assurance", "montant" => "échéance",
        _ => "capital restant dû"
    };
}

/// <summary>
/// Remboursement anticipé d'une partie du capital (décision du titulaire du 2026-11-26, script 170) : capital au centime, supérieur à 0,
/// jamais plus que le capital restant dû ; frais facultatifs (≥ 0, au centime), comptés en charges financières.
/// </summary>
public static class RegleRemboursementAnticipe
{
    public static void Verifier(decimal capital, decimal frais, decimal resteDu)
    {
        if (capital <= 0) throw new RegleMetierException("Le capital remboursé doit être supérieur à 0.", "capital");
        if (!Arrondi.AuCentime(capital)) throw new RegleMetierException("Le capital se saisit au centime près (2 décimales au plus).", "capital");
        if (capital > resteDu)
            throw new RegleMetierException($"Le capital remboursé dépasse le capital restant dû ({Math.Max(resteDu, 0):N2} DA).", "capital");
        if (frais < 0) throw new RegleMetierException("Les frais ne peuvent pas être négatifs.", "frais");
        if (!Arrondi.AuCentime(frais)) throw new RegleMetierException("Les frais se saisissent au centime près (2 décimales au plus).", "frais");
    }
}

/// <summary>
/// Compte du titulaire (fonds propres apportés) : un apport crée une somme que la pharmacie lui doit ; il peut se rembourser plus
/// tard (sortie du coffre ou d'une banque), jamais plus que ce qui lui reste dû.
/// </summary>
public static class RegleCompteTitulaire
{
    public static decimal Solde(IEnumerable<decimal> apports, IEnumerable<decimal> remboursements) => apports.Sum() - remboursements.Sum();

    public static void VerifierRemboursement(decimal montant, decimal soldeDu)
    {
        if (montant <= 0) throw new RegleMetierException("Le montant doit être supérieur à 0.", "montant");
        if (!Arrondi.AuCentime(montant)) throw new RegleMetierException("Le montant se saisit au centime près (2 décimales au plus).", "montant");
        if (montant > soldeDu)
            throw new RegleMetierException($"Le remboursement dépasse ce que la pharmacie doit au titulaire ({Math.Max(soldeDu, 0):N2} DA).", "montant");
    }
}
