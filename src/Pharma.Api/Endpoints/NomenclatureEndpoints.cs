using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using Pharma.Api.Securite;
using Pharma.Application.Catalogue;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class NomenclatureEndpoints
{
    public static void MapNomenclature(this RouteGroupBuilder api)
    {
        // Import du fichier Excel officiel (corps brut ; nom du fichier dans l'en-tête X-Nom-Fichier, encodé URL)
        api.MapPost("/admin/nomenclature", async (HttpRequest requete, ImporterNomenclature importer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                if (requete.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } taille)
                    taille.MaxRequestBodySize = ImporterNomenclature.TailleMax;
                using var memoire = new MemoryStream();
                await requete.Body.CopyToAsync(memoire, ct);
                memoire.Position = 0;
                var nom = requete.Headers["X-Nom-Fichier"].FirstOrDefault() is { Length: > 0 } brut ? Uri.UnescapeDataString(brut) : null;
                return Results.Ok(VersDto(await importer.ExecuterAsync(memoire, nom, user.UtilisateurId(), ct)));
            })
           .ExigerTitulaire();

        api.MapGet("/admin/nomenclature", async (ObtenirEtatNomenclature obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(user.UtilisateurId(), ct);
                return Results.Ok(new EtatNomenclatureDto(e.Version is null ? null : VersDto(e.Version),
                    e.AVerifier.Select(p => new ProduitAVerifierDto(p.ProduitId, p.Nom, p.Dosage, p.Forme, p.Dci, p.NumEnregistrement,
                        p.Classe, p.ClasseProposee, p.Situation, p.Motif)).ToList()));
            })
           .ExigerTitulaire();

        api.MapPut("/admin/produits/{id:long}/classe", async (long id, ClasseProduitRequete req, AppliquerClasseNomenclature appliquer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await appliquer.ExecuterAsync(id, req.Classe, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        // Recherche pour créer un produit à partir de la nomenclature
        api.MapGet("/nomenclature", async (string? q, RechercherNomenclature rechercher, CancellationToken ct) =>
                Results.Ok((await rechercher.ExecuterAsync(q, ct)).Select(m => new MedicamentNomenclatureDto(
                    m.NumEnregistrement, m.Code, m.Dci, m.Marque, m.Forme, m.Dosage, m.Conditionnement, m.Liste,
                    m.Laboratoire, m.Pays, m.Situation, m.ClasseProposee, m.DejaAuCatalogue))))
           .ExigerPermission(Permissions.CatalogueCreer);
    }

    private static VersionNomenclatureDto VersDto(VersionNomenclature v) =>
        new(v.Libelle, v.Fichier, v.EnVigueur, v.NonRenouveles, v.Retraits, v.ImporteeLe, v.ImporteePar);
}
