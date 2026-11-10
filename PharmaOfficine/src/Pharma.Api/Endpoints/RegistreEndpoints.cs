using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Stock;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

internal static class RegistreEndpoints
{
    public static void MapRegistre(this RouteGroupBuilder api)
    {
        var registre = api.MapGroup("/registre").ExigerPermission(Permissions.PsychotropesRegistre);

        registre.MapGet("/produits", async (ListerProduitsRegistre lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(user.UtilisateurId(), ct)).Select(p => new ProduitRegistreDto(p.Id, p.Nom))));

        registre.MapGet("", async (long? produit, DateOnly? du, DateOnly? au, ConsulterRegistre consulter, IHorloge horloge,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await consulter.ExecuterAsync(user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct);
                return Results.Ok(new RegistreDto(r.Du, r.Au, r.Produit,
                    r.Lignes.Select(l => new LigneRegistreDto(l.Numero, l.Date, l.ProduitId, l.Produit, l.NumeroLot, l.Type,
                        RegistreCsv.Libelle(l.Type), l.Entree, l.Sortie, l.SoldeProduit, l.SoldeLot, l.Patient, l.Medecin,
                        l.Ordonnance, l.Fournisseur, l.Reference, l.PorteurNom, l.PorteurAdresse, l.PorteurPiece, l.Employe)).ToList(),
                    r.Balance.Select(b => new BalanceMensuelleDto(b.ProduitId, b.Produit, b.Annee, b.Mois, b.Entrees, b.Sorties, b.SoldeFin)).ToList(),
                    r.NomsMasques));
            });

        registre.MapGet("/export", async (string? format, long? produit, DateOnly? du, DateOnly? au, ExporterRegistre exporter,
                IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (contenu, nom, type) = await exporter.ExecuterAsync(format ?? "pdf", user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct);
                return Results.File(contenu, type, nom);
            });
    }
}
