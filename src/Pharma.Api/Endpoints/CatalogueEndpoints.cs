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
        api.MapGet("/produits", async (string q, int? limite, bool? inactifs, RechercherProduits rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, limite, ct, inactifs == true)).Select(p => p.VersDto())))
           .ExigerPermission(Permissions.StockConsulter);

        // Recherche de la caisse : en stock d'abord, ruptures en bas avec leur remplaçant, même produit moins cher d'un autre laboratoire
        // Par indication (script 104) : « toux » ajoute les produits de l'indication ; indicationId : seulement ceux de cette indication
        api.MapGet("/caisse/produits", async (string? q, int? limite, long? indicationId, RechercherAuComptoir rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, limite, ct, indicationId)).Select(c => new ProduitComptoirDto(c.Produit.VersDto(), c.PrixServi,
                    c.Remplacant?.VersDto(), c.MoinsCher?.VersDto())).ToList()))
           .ExigerPermission(Permissions.CaisseVendre, Permissions.StockConsulter);

        // Création d'un produit absent du catalogue (fiche Stock ou réception)
        // Catégories de produits et classement d'un produit (catégorie, liste d'un psychotrope pour son SHP)
        api.MapGet("/categories-produits", async (ListerCategoriesProduits lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(ct)).Select(c => new CategorieProduitDto(c.Code, c.Libelle, c.Medicament)).ToList()))
           .ExigerConnexion();
        // Remises automatiques : liste ouverte à qui consulte le stock ; création, modification et arrêt : droit caisse.remises_auto (réglé sur l'employé)
        api.MapGet("/remises-automatiques", async (Pharma.Application.Caisse.ListerRemisesAutomatiques lister, CancellationToken ct) =>
            {
                var jour = DateOnly.FromDateTime(DateTime.Today);
                return Results.Ok((await lister.ExecuterAsync(ct)).Select(r => new RemiseAutomatiqueDto(r.Id, r.ProduitId, r.Produit, r.Categorie, r.CategorieLibelle,
                    r.Pourcentage, r.Debut, r.Fin, r.Motif, r.CreePar, r.CreeLe, r.ArreteeLe, r.ArreteePar,
                    r.ArreteeLe is not null ? "arretee" : r.Fin < jour ? "terminee" : r.Debut > jour ? "a_venir" : "en_cours")).ToList());
            })
           .ExigerPermission(Permissions.StockConsulter, Permissions.CaisseVendre, Permissions.CaisseRemisesAuto);
        api.MapPost("/remises-automatiques", async (RemiseAutomatiqueSaisieDto d, Pharma.Application.Caisse.GererRemisesAutomatiques gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await gerer.CreerAsync(VersSaisie(d), user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CaisseRemisesAuto);
        api.MapPut("/remises-automatiques/{id:long}", async (long id, RemiseAutomatiqueSaisieDto d, Pharma.Application.Caisse.GererRemisesAutomatiques gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.ModifierAsync(id, VersSaisie(d), user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseRemisesAuto);
        api.MapPost("/remises-automatiques/{id:long}/arreter", async (long id, Pharma.Application.Caisse.GererRemisesAutomatiques gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.ArreterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseRemisesAuto);

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

        // Fiche d'un produit : lecture et modification (droit de créer un produit ; prix : droit de modifier un prix)
        api.MapGet("/produits/{id:long}/fiche", async (long id, LireFicheProduit lire, CancellationToken ct) =>
            {
                var f = await lire.ExecuterAsync(id, ct);
                return Results.Ok(new FicheProduitModifiableDto(f.Id, f.NomCommercial, f.Dci, f.Dosage, f.Forme, f.CodeBarre, f.PrixPublic, f.TarifReference,
                    f.Remboursable, f.TauxTva, f.Actif, f.Classe));
            })
           .ExigerPermission(Permissions.CatalogueCreer);
        api.MapPut("/produits/{id:long}", async (long id, FicheProduitModifiableDto d, ModifierProduit modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var information = await modifier.ExecuterAsync(new FicheProduitModifiable(id, d.NomCommercial, d.Dci, d.Dosage, d.Forme, d.CodeBarre, d.PrixPublic,
                    d.TarifReference, d.Remboursable, d.TauxTva, d.Actif), user.UtilisateurId(), ct);
                return Results.Ok(new ProduitEnregistreDto(id, information));
            })
           .ExigerPermission(Permissions.CatalogueCreer);

        // Sortie de stock : boîtes périmées ou cassées
        api.MapGet("/stock/produits/{produitId:long}/lots", async (long produitId, Pharma.Application.Stock.ListerLotsEnStock lister, CancellationToken ct) =>
                Results.Ok((await lister.ExecuterAsync(produitId, ct)).Select(VersDto).ToList()))
           .ExigerPermission(Permissions.StockConsulter, Permissions.StockDonner, Permissions.StockDeclarerPerime);
        api.MapGet("/stock/lots/{lotId:long}", async (long lotId, Pharma.Application.Stock.ISortiesStock sorties, CancellationToken ct) =>
                await sorties.LotAsync(lotId, ct) is { } l ? Results.Ok(VersDto(l)) : Results.NotFound(new ErreurDto("Lot introuvable.")))
           .ExigerPermission(Permissions.StockConsulter, Permissions.StockDonner, Permissions.StockDeclarerPerime);
        api.MapPost("/stock/sorties", async (SortieStockRequete req, Pharma.Application.Stock.DeclarerSortieStock declarer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new NombreDto(await declarer.ExecuterAsync(req.LotId, req.Quantite, req.Motif, req.Commentaire, user.UtilisateurId(), ct))))
           // Don sans le droit : le cas d'usage refuse, et le poste propose le code d'un employé qui l'a
           .ExigerPermission(Permissions.StockDeclarerPerime, Permissions.StockDonner, Permissions.StockConsulter);

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
                var cree = await creer.ExecuterAsync(new Pharma.Domain.Catalogue.FicheProduit(d.NomCommercial ?? "", d.Dci ?? "", d.Dosage ?? "", d.Forme ?? "",
                    d.PrixPublic, classe, d.CodeBarre, d.TarifReference, d.Remboursable, d.TauxTva, d.StockMinimum, d.NumEnregistrement,
                    d.Categorie, d.ListePsychotrope), user.UtilisateurId(), ct);
                return Results.Ok(new ProduitEnregistreDto(cree.Id, cree.Information));
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

    private static LotEnStockDto VersDto(Pharma.Application.Stock.LotEnStock l) =>
        new(l.LotId, l.ProduitId, l.Produit, l.NumeroLot, l.DatePeremption, l.Quantite, l.Bloque, l.SousRegistre);

    private static Pharma.Application.Caisse.RemiseAutomatiqueSaisie VersSaisie(RemiseAutomatiqueSaisieDto d) =>
        new(d.ProduitId, d.Categorie, d.Pourcentage, d.Debut, d.Fin, d.Motif);
}
