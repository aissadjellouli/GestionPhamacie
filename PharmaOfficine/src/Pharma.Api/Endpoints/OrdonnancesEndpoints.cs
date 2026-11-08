using Pharma.Api.Securite;
using Pharma.Application.Ordonnances;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class OrdonnancesEndpoints
{
    public static void MapOrdonnances(this RouteGroupBuilder api)
    {
        // Lignes lues par l'OCR du poste → produits du catalogue à faire valider par le caissier
        api.MapPost("/ordonnances/analyser", async (AnalyseOrdonnanceRequete req, AnalyserOrdonnance analyser, CancellationToken ct) =>
            {
                var a = await analyser.ExecuterAsync(req.Lignes, ct);
                return Results.Ok(new AnalyseOrdonnanceDto(
                    a.Lignes.Select(l => new LigneOrdonnanceDto(l.Texte, l.Candidats.Select(c => new ProduitCandidatDto(
                        c.ProduitId, c.NomCommercial, c.Dci, c.Dosage, c.Forme, c.PrixPublic, c.Stock, c.Classe.Code(), c.Score)).ToList(), l.NombreBoites)).ToList(),
                    a.LignesIgnorees));
            })
           .ExigerPermission(Permissions.CaisseVendre);

        // Image de l'ordonnance (corps brut JPEG ou PNG), conservée sur le serveur et liée ensuite à la vente
        api.MapPost("/ordonnances/scans", async (HttpRequest requete, EnregistrerScanOrdonnance enregistrer, CancellationToken ct) =>
            {
                if (requete.ContentLength > EnregistrerScanOrdonnance.TailleMax)
                    throw new RegleMetierException("Image d’ordonnance trop lourde (10 Mo au maximum).");
                using var flux = new MemoryStream();
                await requete.Body.CopyToAsync(flux, ct);
                return Results.Ok(new ScanOrdonnanceDto(await enregistrer.ExecuterAsync(flux.ToArray(), ct)));
            })
           .ExigerPermission(Permissions.CaisseVendre);
    }
}
