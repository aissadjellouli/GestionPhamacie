using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Catalogue;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class CatalogueEndpoints
{
    public static void MapCatalogue(this RouteGroupBuilder api)
    {
        api.MapGet("/produits", async (string q, int? limite, RechercherProduits rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, limite, ct)).Select(p => p.VersDto())))
           .ExigerPermission(Permissions.StockConsulter);

        // Création d'un produit absent du catalogue (fiche Stock ou réception)
        // Catégories de produits et classement d'un produit (catégorie, liste d'un psychotrope pour son SHP)
        api.MapGet("/categories-produits", async (ListerCategoriesProduits lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(c => new CategorieProduitDto(c.Code, c.Libelle, c.Medicament)).ToList()))
           .RequireAuthorization();
        api.MapGet("/produits/{id:long}/classement", async (long id, LireClassementProduit lire, CancellationToken ct) =>
            {
                var c = await lire.ExecuterAsync(id, ct);
                return Results.Ok(new ClassementProduitDto(c.ProduitId, c.Nom, c.Classe, c.Categorie, c.ListePsychotrope));
            })
           .ExigerPermission(Permissions.CatalogueCreer);
        api.MapPut("/produits/{id:long}/classement", async (long id, ClassementProduitRequete req, ModifierClassementProduit modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, req.Categorie, req.ListePsychotrope, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CatalogueCreer);

        // Blocage d'un produit à l'achat et/ou à la vente
        api.MapGet("/produits/bloques", async (LireBlocageProduit lire, CancellationToken ct) =>
                Results.Ok((await lire.BloquesAsync(ct)).Select(b => new BlocageProduitDto(b.ProduitId, b.Nom, b.BloqueAchat, b.BloqueVente, b.Motif)).ToList()))
           .ExigerPermission(Permissions.StockConsulter);
        api.MapGet("/produits/{id:long}/blocage", async (long id, LireBlocageProduit lire, CancellationToken ct) =>
            {
                var b = await lire.ExecuterAsync(id, ct);
                return Results.Ok(new BlocageProduitDto(b.ProduitId, b.Nom, b.BloqueAchat, b.BloqueVente, b.Motif));
            })
           .ExigerPermission(Permissions.ProduitsBloquer);
        api.MapPut("/produits/{id:long}/blocage", async (long id, BlocageProduitRequete req, ModifierBlocageProduit modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(id, req.BloqueAchat, req.BloqueVente, req.Motif, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.ProduitsBloquer);

        api.MapPost("/produits", async (NouveauProduitDto d, CreerProduit creer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                Pharma.Domain.Catalogue.ClasseReglementaire classe;
                try { classe = Pharma.Domain.Commun.CodesMetier.ClasseDepuisCode(d.Classe); }
                catch (ArgumentException) { throw new Pharma.Domain.Commun.RegleMetierException("Classe réglementaire inconnue."); }
                var id = await creer.ExecuterAsync(new Pharma.Domain.Catalogue.FicheProduit(d.NomCommercial ?? "", d.Dci ?? "", d.Dosage ?? "", d.Forme ?? "",
                    d.PrixPublic, classe, d.CodeBarre, d.TarifReference, d.Remboursable, d.TauxTva, d.StockMinimum, d.NumEnregistrement,
                    d.Categorie, d.ListePsychotrope), user.UtilisateurId(), ct);
                return Results.Ok(new IdentifiantDto(id));
            })
           .ExigerPermission(Permissions.CatalogueCreer);

        api.MapGet("/dci", async (string? q, RechercherDci rechercher, CancellationToken ct) => Results.Ok(await rechercher.ExecuterAsync(q, ct)))
           .ExigerPermission(Permissions.CatalogueCreer);

        // exclure : lots déjà au panier du poste (le lot suivant dans l'ordre FEFO est alors proposé)
        api.MapGet("/produits/{id:long}/lot-fefo", async (long id, long[]? exclure, ObtenirLotFefo obtenir, CancellationToken ct) =>
            {
                var resultat = await obtenir.ExecuterAsync(id, exclure, ct);
                return resultat.Lot is { } lot
                    ? Results.Ok(lot.VersDto(resultat.Ecartes))
                    : Results.NotFound(new AucunLotDto(
                        "Aucun lot vendable : rupture, péremption ou blocage.",
                        resultat.Ecartes.Select(e => e.VersDto()).ToList()));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        api.MapGet("/produits/{id:long}/alternatives", async (long id, ListerAlternatives lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(id, ct)).Select(a => a.VersDto())))
           .ExigerPermission(Permissions.CaisseVendre);
    }
}
