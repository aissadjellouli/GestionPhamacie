using Pharma.Domain.Commun;

namespace Pharma.Domain.Achats;

/// <summary>
/// Supplément d'honoraire du pharmacien (SHP), par boîte, selon la liste du médicament (décret exécutif 98-44 du 1er février 1998,
/// tel que cité par les publications professionnelles ; montants retrouvés sur les factures des grossistes) :
/// liste I (ex-tableaux A et B) 2,50 DA, liste II (ex-tableau C) 1,50 DA, hors liste 0.
/// Les stupéfiants relèvent de l'ex-tableau B, donc de la liste I. Psychotropes : 2,50 DA par défaut, à confirmer ; le montant
/// reste modifiable sur chaque ligne de réception, comme il est imprimé sur la facture.
/// </summary>
public static class RegleShp
{
    public const decimal ListeI = 2.50m;
    public const decimal ListeII = 1.50m;

    public static decimal ParBoite(string? classe) => classe switch
    {
        "liste_i" or "stupefiant" or "psychotrope" => ListeI,
        "liste_ii" => ListeII,
        _ => 0m
    };
}

/// <summary>Totaux d'une facture d'achat, calculés comme sur les factures des grossistes.</summary>
public sealed record TotauxFactureAchat(decimal MontantHt, decimal Remise, decimal NetHt, decimal Tva, decimal Timbre, decimal NetAPayer,
    decimal ShpTotal, decimal PpaTotal);

/// <summary>
/// Facture d'achat : la remise de chaque ligne (en %) est retirée en pied de facture ; le coût d'une boîte est son prix net de
/// remise ; le net à payer au grossiste est le net HT, plus la TVA, plus le timbre. Le SHP et le PPA sont indiqués à part, comme
/// sur la facture : ils ne sont pas dus au grossiste. Vérifié sur une facture réelle : lignes à 5 % de remise pour 3 162,10 DA
/// → remise 158,11 DA, net HT 10 521,74 DA.
/// </summary>
public static class RegleFactureAchat
{
    private static decimal Arrondi(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    /// <summary>Prix unitaire net de remise, arrondi au centime : coût d'une boîte en stock.</summary>
    public static decimal PrixNet(decimal prixHt, decimal remisePct) => Arrondi(prixHt * (1 - remisePct / 100m));

    /// <summary>Marge sur le prix d'achat, comme la colonne « MGE » des factures : PPA ÷ prix d'achat − 1 (null sans PPA ou sans prix).</summary>
    public static decimal? MargePct(decimal prixHt, decimal? ppa) =>
        ppa is > 0 && prixHt > 0 ? Arrondi((ppa.Value / prixHt - 1) * 100) : null;

    public static TotauxFactureAchat Totaux(IReadOnlyList<LigneReceptionSaisie> lignes, IReadOnlyDictionary<long, decimal> tauxTva, decimal timbre)
    {
        var ht = lignes.Sum(l => Arrondi(l.Quantite * l.PrixAchatHt));
        var remise = Arrondi(lignes.Sum(l => l.Quantite * l.PrixAchatHt * l.RemisePct / 100m));
        var tva = Arrondi(lignes.Sum(l => l.Quantite * l.PrixAchatHt * (1 - l.RemisePct / 100m) * tauxTva.GetValueOrDefault(l.ProduitId) / 100m));
        var shp = Arrondi(lignes.Sum(l => l.Quantite * (l.Shp ?? 0)));
        var ppa = Arrondi(lignes.Sum(l => l.Quantite * (l.Ppa ?? 0)));
        return new TotauxFactureAchat(ht, remise, ht - remise, tva, timbre, ht - remise + tva + timbre, shp, ppa);
    }

    public static void Verifier(IReadOnlyList<LigneReceptionSaisie> lignes, decimal timbre)
    {
        if (timbre < 0) throw new RegleMetierException("Le timbre ne peut pas être négatif.", "timbre");
        foreach (var l in lignes)
        {
            if (l.RemisePct is < 0 or > 100) throw new RegleMetierException($"{l.Nom} : la remise doit être comprise entre 0 et 100 %.");
            if (l.Ppa is < 0) throw new RegleMetierException($"{l.Nom} : le PPA ne peut pas être négatif.");
            if (l.Shp is < 0) throw new RegleMetierException($"{l.Nom} : le SHP ne peut pas être négatif.");
        }
    }
}
