using Pharma.Domain.Commun;

namespace Pharma.Domain.Achats;

/// <summary>
/// Supplément d'honoraire du pharmacien (SHP), par boîte, selon la liste du médicament (décret exécutif 98-44 du 1er février 1998,
/// tel que cité par les publications professionnelles ; montants retrouvés sur les factures des grossistes) :
/// liste I (ex-tableaux A et B) 2,50 DA, liste II (ex-tableau C) 1,50 DA, hors liste 0.
/// Les stupéfiants relèvent de l'ex-tableau B, donc de la liste I. Un psychotrope suit la liste (I ou II) indiquée sur sa fiche ;
/// non renseignée : 0 jusqu'à ce qu'elle le soit. Le montant reste modifiable sur chaque ligne de réception.
/// </summary>
public static class RegleShp
{
    public const decimal ListeI = 2.50m;
    public const decimal ListeII = 1.50m;

    public static decimal ParBoite(string? classe, string? listePsychotrope = null) => classe switch
    {
        "liste_i" or "stupefiant" => ListeI,
        "liste_ii" => ListeII,
        "psychotrope" => listePsychotrope switch { "liste_i" => ListeI, "liste_ii" => ListeII, _ => 0m },
        _ => 0m
    };

    public static decimal ParBoite(Catalogue.ClasseReglementaire classe, string? listePsychotrope) => classe switch
    {
        Catalogue.ClasseReglementaire.ListeI or Catalogue.ClasseReglementaire.Stupefiant => ListeI,
        Catalogue.ClasseReglementaire.ListeII => ListeII,
        Catalogue.ClasseReglementaire.Psychotrope => ParBoite("psychotrope", listePsychotrope),
        _ => 0m
    };

    /// <summary>Classe du produit telle qu'écrite dans le contrôle du SHP de la réception (hors liste : parapharmacie).</summary>
    public static string LibelleClasse(string? classe) => classe switch
    {
        "liste_i" => "liste I",
        "liste_ii" => "liste II",
        "psychotrope" => "psychotrope",
        "stupefiant" => "stupéfiant (liste I)",
        _ => "parapharmacie"
    };

    /// <summary>
    /// Contrôle du SHP d'une ligne de réception (décision du 2026-11-15) : le SHP de la facture du grossiste (à part du PPA) doit être
    /// celui de la classe du produit. Écart : la ligne est signalée, sans bloquer l'entrée en stock ; le lot garde le SHP de la facture.
    /// Null si le SHP n'est pas saisi ou s'il est conforme.
    /// </summary>
    public static EcartShp? Controler(long produitId, string produit, string? classe, decimal? shpFacture, decimal shpAttendu) =>
        shpFacture is { } f && Math.Abs(f - shpAttendu) >= 0.01m ? new EcartShp(produitId, produit, classe ?? "", f, shpAttendu) : null;
}

/// <summary>SHP de la facture différent du SHP attendu pour la classe du produit (signalement non bloquant).</summary>
public sealed record EcartShp(long ProduitId, string Produit, string Classe, decimal ShpFacture, decimal ShpAttendu)
{
    public string Message =>
        $"{Produit} : SHP facture {ShpFacture:0.00} DA ≠ SHP attendue {ShpAttendu:0.00} DA pour un produit {RegleShp.LibelleClasse(Classe)} : vérifier la classe du produit ou la facture.";
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
        // Montants de la facture : boîtes facturées seulement (les unités gratuites ne sont pas facturées)
        var ht = lignes.Sum(l => Arrondi(l.Facturees * l.PrixAchatHt));
        var remise = Arrondi(lignes.Sum(l => l.Facturees * l.PrixAchatHt * l.RemisePct / 100m));
        var tva = Arrondi(lignes.Sum(l => l.Facturees * l.PrixAchatHt * (1 - l.RemisePct / 100m) * tauxTva.GetValueOrDefault(l.ProduitId) / 100m));
        var shp = Arrondi(lignes.Sum(l => l.Facturees * (l.Shp ?? 0)));
        var ppa = Arrondi(lignes.Sum(l => l.Facturees * (l.Ppa ?? 0)));
        return new TotauxFactureAchat(ht, remise, ht - remise, tva, timbre, ht - remise + tva + timbre, shp, ppa);
    }

    /// <summary>Écart maximal entre la date de la facture du grossiste et la livraison (D-6, 2026-11-15).</summary>
    public const int JoursMaxFactureAvantLivraison = 90;

    /// <summary>
    /// Date de la facture du grossiste (facultative, D-6 du 2026-11-15) : jamais dans le futur, jamais plus de 90 jours avant la
    /// livraison (réception du jour).
    /// </summary>
    public static void VerifierDateFacture(DateOnly? dateFacture, DateOnly livraison)
    {
        if (dateFacture is not { } d) return;
        if (d > livraison)
            throw new RegleMetierException($"Date de facture du {d:dd/MM/yyyy} : elle ne peut pas être dans le futur.", "date_facture");
        if (d < livraison.AddDays(-JoursMaxFactureAvantLivraison))
            throw new RegleMetierException(
                $"Date de facture du {d:dd/MM/yyyy} : plus de {JoursMaxFactureAvantLivraison} jours avant la livraison du {livraison:dd/MM/yyyy}. Vérifiez la date.", "date_facture");
    }

    public static void Verifier(IReadOnlyList<LigneReceptionSaisie> lignes, decimal timbre)
    {
        if (timbre < 0) throw new RegleMetierException("Le timbre ne peut pas être négatif.", "timbre");
        foreach (var l in lignes)
        {
            if (l.RemisePct < 0) throw new RegleMetierException($"{l.Nom} : la remise ne peut pas être négative (0 à 100 %).");
            if (l.RemisePct > 100) throw new RegleMetierException($"{l.Nom} : la remise doit être comprise entre 0 et 100 %.");
            if (l.Ppa is < 0) throw new RegleMetierException($"{l.Nom} : le PPA ne peut pas être négatif.");
            if (l.Shp is < 0) throw new RegleMetierException($"{l.Nom} : le SHP ne peut pas être négatif.");
            if (l.Ug < 0 || l.Ug > l.Quantite)
                throw new RegleMetierException($"{l.Nom} : les unités gratuites (UG) sont comprises entre 0 et le nombre de boîtes reçues ({l.Quantite}).");
            if (l.RemplacerPrixCatalogue && l.Ppa is not > 0)
                throw new RegleMetierException($"{l.Nom} : indiquez le PPA facturé pour remplacer le prix du catalogue.");
        }
    }
}
