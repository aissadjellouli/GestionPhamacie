using Pharma.Domain.Commun;

namespace Pharma.Domain.Caisse;

/// <summary>Coupures comptées à la clôture. « autres » se saisit en montant total.</summary>
public static class Coupures
{
    public static readonly IReadOnlyList<(string Code, decimal Valeur, string Libelle)> Toutes =
    [
        ("b2000", 2000m, "Billets de 2 000 DA"),
        ("b1000", 1000m, "Billets de 1 000 DA"),
        ("b500", 500m, "Billets de 500 DA"),
        ("b200", 200m, "Billets de 200 DA"),
        ("p100", 100m, "Pièces de 100 DA"),
        ("autres", 1m, "Autres pièces (total DA)")
    ];
}

public sealed record LigneComptage(string Coupure, int Nombre, decimal Montant);

public sealed record Comptage(IReadOnlyList<LigneComptage> Lignes)
{
    public decimal Total => Lignes.Sum(l => l.Montant);

    /// <summary>Nombres saisis par coupure ; pour « autres », le montant total des pièces.</summary>
    public static Comptage Depuis(IReadOnlyDictionary<string, decimal> saisies)
    {
        var lignes = new List<LigneComptage>();
        foreach (var (code, valeur, libelle) in Coupures.Toutes)
        {
            var saisie = saisies.TryGetValue(code, out var v) ? v : 0;
            if (saisie < 0)
                throw new RegleMetierException($"{libelle} : la valeur ne peut pas être négative.");
            if (code == "autres")
                lignes.Add(new LigneComptage(code, saisie > 0 ? 1 : 0, saisie));
            else
            {
                if (saisie != Math.Floor(saisie))
                    throw new RegleMetierException($"{libelle} : indiquez un nombre entier.");
                lignes.Add(new LigneComptage(code, (int)saisie, saisie * valeur));
            }
        }
        return new Comptage(lignes);
    }
}

/// <summary>Résultat de la clôture : attendu = fond de caisse + ventes en espèces ; écart = compté − attendu.</summary>
public sealed record ResultatCloture(decimal FondCaisse, decimal VentesEspeces, decimal Attendu, decimal Compte, decimal Ecart, bool AuDelaDuSeuil);

public class SessionCaisse
{
    public long Id { get; init; }
    public long UtilisateurId { get; init; }
    public string Poste { get; init; } = "";
    public DateTime OuverteLe { get; init; }
    public decimal FondCaisse { get; init; }
    public DateTime? FermeeLe { get; init; }

    public bool EstOuverte => FermeeLe is null;

    public static void VerifierFond(decimal fond)
    {
        if (fond < 0)
            throw new RegleMetierException("Le fond de caisse ne peut pas être négatif.");
    }

    public ResultatCloture Cloturer(Comptage comptage, decimal ventesEspeces, decimal seuilEcart)
    {
        if (!EstOuverte)
            throw new RegleMetierException("Cette session de caisse est déjà clôturée.");
        var attendu = FondCaisse + ventesEspeces;
        var ecart = comptage.Total - attendu;
        return new ResultatCloture(FondCaisse, ventesEspeces, attendu, comptage.Total, ecart, Math.Abs(ecart) > seuilEcart);
    }
}
