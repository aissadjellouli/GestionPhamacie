using Pharma.Api.Securite;
using Microsoft.AspNetCore.Http.Features;
using Pharma.Application.Ged;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class GedEndpoints
{
    public static void MapGed(this RouteGroupBuilder api)
    {
        // Document produit par un poste (corps brut ; nom du fichier dans l'en-tête X-Nom-Fichier, encodé URL) : rangé dans la GED du serveur
        api.MapPost("/ged/{type}", async (string type, HttpRequest requete, DeposerDocumentGed deposer, System.Security.Claims.ClaimsPrincipal user, CancellationToken ct) =>
            {
                if (requete.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } taille)
                    taille.MaxRequestBodySize = DeposerDocumentGed.TailleMax + 1;
                using var memoire = new MemoryStream();
                await requete.Body.CopyToAsync(memoire, ct);
                var nom = requete.Headers["X-Nom-Fichier"].FirstOrDefault() is { Length: > 0 } brut ? Uri.UnescapeDataString(brut) : null;
                var chemin = await deposer.ExecuterAsync(user.UtilisateurId(), type, nom, memoire.ToArray(), ct);
                return Results.Ok(new DocumentGedDto(chemin));
            })
           .ExigerConnexion();
    }
}
