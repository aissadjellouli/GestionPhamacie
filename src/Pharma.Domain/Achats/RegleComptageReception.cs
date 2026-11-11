using Pharma.Domain.Commun;
using Pharma.Domain.Stock;

namespace Pharma.Domain.Achats;

/// <summary>Boîtes d'un lot comptées au déballage (réception à l'aveugle), sans voir le bon de livraison.</summary>
public sealed record LigneComptee(
    long ProduitId, string Nom, string NumeroLot, DateOnly DatePeremption, int Quantite,
    string? Gtin = null, IReadOnlyList<string>? NumerosSerie = null);

/// <summary>Lot dont le nombre de boîtes facturées (UG comprises) diffère du compté.</summary>
public sealed record EcartComptage(long ProduitId, string Nom, string NumeroLot, int Facture, int Compte)
{
    public int Manquantes => Math.Max(Facture - Compte, 0);
    public int EnTrop => Math.Max(Compte - Facture, 0);
}

/// <summary>Boîtes facturées mais absentes du carton : sorties du stock après l'entrée de la ligne (index dans Lignes).</summary>
public sealed record LigneManquante(int Index, int Boites);

/// <summary>
/// Lignes à entrer en stock : la facture telle que saisie, avec les Data Matrix du comptage. Boîtes en trop : unités gratuites
/// à 0 DA ; boîtes manquantes : entrées comme facturées puis sorties (Manquants) ; la dette suit toujours la facture.
/// </summary>
public sealed record FusionComptage(IReadOnlyList<LigneReceptionSaisie> Lignes, IReadOnlyList<EcartComptage> Ecarts, IReadOnlyList<LigneManquante> Manquants)
{
    public int BoitesManquantes => Ecarts.Sum(e => e.Manquantes);
    public int BoitesEnTrop => Ecarts.Sum(e => e.EnTrop);
}

public static class RegleComptageReception
{
    /// <summary>Un lot : produit et n° de lot (sans tenir compte des majuscules ni des espaces en bordure).</summary>
    public static (long, string) Cle(long produitId, string numeroLot) => (produitId, RegleLot.Normaliser(numeroLot).ToUpperInvariant());

    /// <summary>Comptage : BL, au moins une ligne, lot et date comme une réception, un lot une seule fois, séries ≤ boîtes.</summary>
    public static void VerifierComptage(string? numeroBl, IReadOnlyList<LigneComptee> lignes, DateOnly aujourdhui)
    {
        RegleAchats.VerifierReception(numeroBl, lignes.Select(l =>
            new LigneReceptionSaisie(l.ProduitId, l.Nom, l.NumeroLot, l.DatePeremption, l.Quantite, 0m, l.Gtin, l.NumerosSerie)).ToList(), aujourdhui);
        if (lignes.GroupBy(l => Cle(l.ProduitId, l.NumeroLot)).Any(g => g.Count() > 1))
            throw new RegleMetierException("Un même lot figure deux fois dans le comptage : regroupez les boîtes sur une ligne.");
    }

    /// <summary>Compare la facture saisie au comptage et prépare l'entrée en stock (voir FusionComptage).</summary>
    public static FusionComptage Fusionner(IReadOnlyList<LigneReceptionSaisie> facture, IReadOnlyList<LigneComptee> comptees)
    {
        if (facture.GroupBy(l => Cle(l.ProduitId, l.NumeroLot)).Any(g => g.Count() > 1))
            throw new RegleMetierException("Un même lot figure deux fois : regroupez les quantités sur une ligne.");
        var parLot = comptees.ToDictionary(c => Cle(c.ProduitId, c.NumeroLot));
        var lignes = new List<LigneReceptionSaisie>();
        var ecarts = new List<EcartComptage>();
        var manquants = new List<LigneManquante>();

        foreach (var f in facture)
        {
            var c = parLot.GetValueOrDefault(Cle(f.ProduitId, f.NumeroLot));
            var compte = c?.Quantite ?? 0;
            // Les Data Matrix viennent du comptage (boîtes vues), jamais de la saisie de la facture
            var ligne = f with { Gtin = c?.Gtin ?? f.Gtin, NumerosSerie = c?.NumerosSerie is { Count: > 0 } s ? s : null };
            if (compte > f.Quantite)
                ligne = ligne with { Quantite = compte, Ug = f.Ug + (compte - f.Quantite) };
            else if (compte < f.Quantite)
                manquants.Add(new LigneManquante(lignes.Count, f.Quantite - compte));
            if (compte != f.Quantite) ecarts.Add(new EcartComptage(f.ProduitId, c?.Nom ?? f.Nom, f.NumeroLot, f.Quantite, compte));
            lignes.Add(ligne);
        }
        // Lots comptés absents de la facture : boîtes en trop, entrées à 0 DA
        var factures = facture.Select(f => Cle(f.ProduitId, f.NumeroLot)).ToHashSet();
        foreach (var c in comptees.Where(c => !factures.Contains(Cle(c.ProduitId, c.NumeroLot))))
        {
            lignes.Add(new LigneReceptionSaisie(c.ProduitId, c.Nom, c.NumeroLot, c.DatePeremption, c.Quantite, 0m, c.Gtin,
                c.NumerosSerie is { Count: > 0 } s ? s : null, Ug: c.Quantite));
            ecarts.Add(new EcartComptage(c.ProduitId, c.Nom, c.NumeroLot, 0, c.Quantite));
        }
        return new FusionComptage(lignes, ecarts, manquants);
    }
}
