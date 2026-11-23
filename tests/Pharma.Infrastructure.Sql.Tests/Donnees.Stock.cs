using Pharma.Application.Achats;
using Pharma.Domain.Achats;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>Fabrique du lot 3 (stock) : inventaires comptés, réception de marchandise à boîtes étiquetées.</summary>
public sealed partial class Donnees
{
    /// <summary>Inventaire créé par <see cref="InventaireAsync"/> : son identifiant et celui de chaque ligne (dans l'ordre des lots donnés).</summary>
    public sealed record InventaireEssai(long Id, IReadOnlyList<long> Lignes);

    /// <summary>
    /// Inventaire « en cours » (rayon unique) avec une ligne par lot, déjà comptée (statut « compte », théorique = quantité du lot au départ).
    /// </summary>
    public async Task<InventaireEssai> InventaireAsync(long utilisateurId, params (long LotId, int Compte)[] lots)
    {
        var inventaire = await InsererAsync(
            "INSERT INTO inventaire (rayon, commence_par) VALUES (@rayon, @u) RETURNING id",
            ("rayon", $"Rayon Essai {Suivant()}"), ("u", utilisateurId));
        var lignes = new List<long>();
        foreach (var (lotId, compte) in lots)
            lignes.Add(await InsererAsync(
                "INSERT INTO ligne_inventaire (inventaire_id, lot_id, theorique, compte, compte_par, compte_le, statut) " +
                "SELECT @i, l.id, l.quantite_disponible, @c, @u, now(), 'compte' FROM lot l WHERE l.id = @l RETURNING id",
                ("i", inventaire), ("l", lotId), ("c", compte), ("u", utilisateurId)));
        return new InventaireEssai(inventaire, lignes);
    }

    /// <summary>Réception d'une seule ligne (sans Data Matrix : les boîtes reçoivent une étiquette interne), avec les boîtes manquantes demandées.</summary>
    public static NouvelleReception ReceptionEssai(long fournisseurId, long utilisateurId, long produitId, int quantite, string numeroBl, int boitesManquantes = 0)
        => new(fournisseurId, null, numeroBl, null,
            [new LigneReceptionSaisie(produitId, "Produit Essai", $"LOT-REC-{Suivant()}", DateOnly.FromDateTime(DateTime.Today.AddYears(1)), quantite, 100m)],
            new Dictionary<long, decimal>(), quantite * 100m, utilisateurId,
            Manquants: boitesManquantes > 0 ? [new LigneManquante(0, boitesManquantes)] : null);
}
