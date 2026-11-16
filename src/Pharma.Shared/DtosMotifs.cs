namespace Pharma.Shared;

// ---- Motifs réglables (script 094) ----
/// <summary>Motif d'une liste réglable : tiroir, attente, charge, sortie_stock, retour_fournisseur. Systeme : jamais désactivé.</summary>
public sealed record MotifReglableDto(long Id, string Liste, string Code, string Libelle, bool Actif, bool Systeme, string Effet = "aucun", string Detail = "aucun", bool DuCentral = false);
/// <summary>Ajout (Liste, Libelle ; tiroir : Effet et Detail, fixés à la création) ou modification (Libelle, Actif) d'un motif.</summary>
public sealed record SaisieMotifReglableDto(string Liste, string Libelle, bool Actif = true, string? Effet = null, string? Detail = null);
/// <summary>Produit dû au patient, gardé avec une vente mise en attente pour « produit manquant » (aucune commande créée).</summary>
public sealed record ProduitDuDto(long ProduitId, string Nom, int Quantite);

/// <summary>Nombre de vignettes attendues sur un volet Chifa : somme des boîtes des lignes du volet (quantités positives).</summary>
public static class VignettesChifa
{
    public static int Nombre(IEnumerable<int> quantites) => quantites.Where(q => q > 0).Sum();
    public static int Nombre(VoletChifaDto volet) => Nombre(volet.Lignes.Select(l => l.Quantite));
}
