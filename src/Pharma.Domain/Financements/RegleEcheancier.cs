using Pharma.Domain.Commun;

namespace Pharma.Domain.Financements;

/// <summary>
/// Échéance d'un prêt : Montant = Capital + Interets + Tva + Marge. Tva et Marge facultatives (0) : la marge est celle d'un
/// financement islamique (mourabaha). CapitalRestant : capital restant dû tel que le tableau de la banque l'indique.
/// </summary>
public sealed record LigneEcheancier(DateOnly Date, decimal Capital, decimal Interets, decimal Tva, decimal Marge, decimal Montant, decimal CapitalRestant)
{
    /// <summary>Part comptée en charge financière : intérêts, TVA et marge.</summary>
    public decimal Charges => Interets + Tva + Marge;
}

/// <summary>Incohérence d'un échéancier. Ligne : n° de la ligne (à partir de 1), 0 pour l'échéancier entier. Champ : colonne en cause.</summary>
public sealed record AnomalieEcheancier(int Ligne, string Champ, string Message);

/// <summary>
/// Contrôles d'un échéancier avant son enregistrement (saisi, importé d'Excel ou de CSV, ou lu dans un PDF ou un scan) :
/// chaque ligne au centime, capital + intérêts + TVA + marge = échéance ; dates strictement croissantes, pas avant le prêt ;
/// capital restant dû jamais en hausse ; somme du capital = montant du prêt. Tant qu'une incohérence reste, l'enregistrement est refusé.
/// </summary>
public static class RegleEcheancier
{
    public static IReadOnlyList<AnomalieEcheancier> Controler(decimal montantPret, DateOnly datePret, IReadOnlyList<LigneEcheancier> lignes)
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
                         ("montant", l.Montant), ("capital_restant", l.CapitalRestant) })
            {
                if (valeur < 0) anomalies.Add(new(n, champ, $"Ligne {n} : montant négatif ({Nom(champ)})."));
                else if (!Arrondi.AuCentime(valeur)) anomalies.Add(new(n, champ, $"Ligne {n} : {Nom(champ)} au centime près (2 décimales au plus)."));
            }
            if (l.Montant <= 0) anomalies.Add(new(n, "montant", $"Ligne {n} : l’échéance doit être supérieure à 0."));
            var somme = l.Capital + l.Interets + l.Tva + l.Marge;
            if (somme != l.Montant)
                anomalies.Add(new(n, "montant", $"Ligne {n} : capital + intérêts + TVA + marge = {somme:N2} DA, l’échéance indique {l.Montant:N2} DA (écart {l.Montant - somme:N2} DA)."));
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
        if (capital != montantPret)
            anomalies.Add(new(0, "capital", $"La somme du capital ({capital:N2} DA) diffère du montant du prêt ({montantPret:N2} DA) : écart {montantPret - capital:N2} DA."));
        return anomalies;
    }

    /// <summary>Refuse l'échéancier tant qu'il reste une incohérence (message : la première, et le nombre des autres).</summary>
    public static void Exiger(decimal montantPret, DateOnly datePret, IReadOnlyList<LigneEcheancier> lignes)
    {
        var anomalies = Controler(montantPret, datePret, lignes);
        if (anomalies.Count == 0) return;
        var autres = anomalies.Count > 1 ? $" (et {anomalies.Count - 1} autre(s) incohérence(s))" : "";
        throw new RegleMetierException($"Échéancier incohérent : {anomalies[0].Message}{autres} Corrigez-le avant de l’enregistrer.", "echeancier");
    }

    /// <summary>Paiement d'une échéance : le capital diminue la dette envers la banque ; intérêts, TVA et marge sont des charges financières.</summary>
    public static (decimal Capital, decimal Charges) Parts(LigneEcheancier l) => (l.Capital, l.Charges);

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
        "capital" => "capital", "interets" => "intérêts", "tva" => "TVA", "marge" => "marge", "montant" => "échéance", _ => "capital restant dû"
    };
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
