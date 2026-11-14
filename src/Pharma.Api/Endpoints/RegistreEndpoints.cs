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

        // En-tête de l'écran : nombre d'inscriptions et balance mensuelle, calculés par la base ; période sans limite
        registre.MapGet("", async (long? produit, DateOnly? du, DateOnly? au, ConsulterRegistre consulter, IHorloge horloge,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await consulter.SyntheseAsync(user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct);
                return Results.Ok(new RegistreDto(r.Du, r.Au, r.Produit, r.Inscriptions,
                    r.Balance.Select(b => new BalanceMensuelleDto(b.ProduitId, b.Produit, b.Annee, b.Mois, b.Entrees, b.Sorties, b.SoldeFin, b.SoldeReporte)).ToList(),
                    r.NomsMasques));
            });

        // Inscriptions paginées et filtrées par la base (règle des listes) : produit, période, opération ; numéros dans l'ordre du registre
        registre.MapGet("/inscriptions", async (long? produit, DateOnly? du, DateOnly? au, string? operation, int? page, int? taille,
                ConsulterRegistre consulter, IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await consulter.InscriptionsAsync(user.UtilisateurId(), produit, du, au, operation, DemandePage.Lire(page, taille),
                    horloge.Aujourdhui, ct);
                var operations = r.Operations.Select(t => new CodeLibelleDto(t, RegistreCsv.Libelle(t)))
                    .OrderBy(o => o.Libelle, StringComparer.CurrentCulture).ToList();
                var p = r.Page.Vers(l => new LigneRegistreDto(l.Numero, l.Date, l.ProduitId, l.Produit, l.NumeroLot, l.Type,
                    RegistreCsv.Libelle(l.Type), l.Entree, l.Sortie, l.SoldeProduit, l.SoldeLot, l.Patient, l.Medecin,
                    l.Ordonnance, l.Fournisseur, l.Reference, l.PorteurNom, l.PorteurAdresse, l.PorteurPiece, l.Employe, l.PatientId));
                return Results.Ok(new InscriptionsRegistreDto(new PageDto<LigneRegistreDto>(p.Lignes, p.Total, p.Numero, p.Taille), operations, r.NomsMasques));
            });

        registre.MapGet("/export", async (string? format, long? produit, DateOnly? du, DateOnly? au, ExporterRegistre exporter,
                IHorloge horloge, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (contenu, nom, type) = await exporter.ExecuterAsync(format ?? "pdf", user.UtilisateurId(), produit, du, au, horloge.Aujourdhui, ct);
                return Results.File(contenu, type, nom);
            });
    }
}
