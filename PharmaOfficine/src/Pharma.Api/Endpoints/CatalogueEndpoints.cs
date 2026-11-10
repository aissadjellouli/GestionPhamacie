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
        api.MapPost("/produits", async (NouveauProduitDto d, CreerProduit creer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                Pharma.Domain.Catalogue.ClasseReglementaire classe;
                try { classe = Pharma.Domain.Commun.CodesMetier.ClasseDepuisCode(d.Classe); }
                catch (ArgumentException) { throw new Pharma.Domain.Commun.RegleMetierException("Classe réglementaire inconnue."); }
                var id = await creer.ExecuterAsync(new Pharma.Domain.Catalogue.FicheProduit(d.NomCommercial ?? "", d.Dci ?? "", d.Dosage ?? "", d.Forme ?? "",
                    d.PrixPublic, classe, d.CodeBarre, d.TarifReference, d.Remboursable, d.TauxTva, d.StockMinimum, d.NumEnregistrement), user.UtilisateurId(), ct);
                return Results.Ok(new IdentifiantDto(id));
            })
           .ExigerPermission(Permissions.CatalogueCreer);

        api.MapGet("/dci", async (string? q, RechercherDci rechercher, CancellationToken ct) => Results.Ok(await rechercher.ExecuterAsync(q, ct)))
           .ExigerPermission(Permissions.CatalogueCreer);

        api.MapGet("/produits/{id:long}/lot-fefo", async (long id, ObtenirLotFefo obtenir, CancellationToken ct) =>
            {
                var resultat = await obtenir.ExecuterAsync(id, ct);
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
