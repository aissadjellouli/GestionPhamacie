using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Volet Chifa : compléments (2026-11-15, script 100) : centres payeurs (liste : droit de vendre ; ajout et modification : admin.parametres),
/// informations Chifa d'une vente (droit de vendre), exclusion du traitement et rétablissement (chifa.exclure), liste des ventes Chifa.
/// </summary>
internal static class ChifaEndpoints
{
    public static void MapChifaComplements(this RouteGroupBuilder api)
    {
        api.MapGet("/centres-payeurs", async (long? organismeId, bool? actif, string? q, int? page, int? taille, CentresPayeurs centres,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await centres.ListerAsync(organismeId, actif, q, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<CentrePayeurDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/centres-payeurs", async (CentrePayeurSaisie s, CentresPayeurs centres, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await centres.EnregistrerAsync(null, s.OrganismeId, s.Code, s.Nom, s.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres)
           .GererErreursMetier();

        api.MapPut("/centres-payeurs/{id:long}", async (long id, CentrePayeurSaisie s, CentresPayeurs centres, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await centres.EnregistrerAsync(id, s.OrganismeId, s.Code, s.Nom, s.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres)
           .GererErreursMetier();

        // Ventes Chifa : exclues = non (défaut), oui, tous
        api.MapGet("/chifa/ventes", async (string? q, long? centrePayeurId, string? exclues, int? page, int? taille, ConsulterVentesChifa consulter,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await consulter.ListerAsync(new FiltreVentesChifa(q, centrePayeurId, FiltreVentesChifa.Lire(exclues)),
                    DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<VenteChifaDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapGet("/chifa/ventes/{id:long}", async (long id, ConsulterVentesChifa consulter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(Dto(await consulter.LireAsync(id, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapGet("/chifa/employes", async (ConsulterVentesChifa consulter, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok((await consulter.EmployesAsync(user.UtilisateurId(), ct)).Select(e => new EmployeChoixDto(e.Id, e.Nom)).ToList()))
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPut("/ventes/{id:long}/chifa", async (long id, ChifaInfosSaisie s, EnregistrerInfosChifa enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await enregistrer.ExecuterAsync(id, new InfosChifa(s.DateSoins, s.CentrePayeurId, s.SaisiParId, s.MajorationType, s.MajorationMontant),
                    s.RetenirCentre, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre)
           .GererErreursMetier();

        api.MapPost("/ventes/{id:long}/chifa/exclusion", async (long id, ExclusionChifaRequete r, ExclureVenteChifa exclure, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await exclure.ExclureAsync(id, r.MotifCode, r.Texte, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.ChifaExclure)
           .GererErreursMetier();

        api.MapDelete("/ventes/{id:long}/chifa/exclusion", async (long id, ExclureVenteChifa exclure, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await exclure.RetablirAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.ChifaExclure)
           .GererErreursMetier();
    }

    private static CentrePayeurDto Dto(CentrePayeur c) => new(c.Id, c.OrganismeId, c.OrganismeCode, c.Code, c.Nom, c.Actif);

    private static VenteChifaDto Dto(VenteChifa v) => new(v.Id, v.Numero, v.Date, v.Statut, v.DansBordereau, v.Rattachee, v.PatientId, v.Patient, v.NumAssure,
        v.OrganismeId, v.Organisme, v.Vendeur, v.VendeurId, v.Total, v.PartOrganisme, v.FactureChifa,
        v.Infos.DateSoins, v.Infos.CentrePayeurId, v.CentrePayeur, v.Infos.SaisiPar, v.SaisiPar, v.Infos.MajorationCode, v.Majoration, v.Infos.MajorationMontant,
        v.ExclueLe, v.ExcluePar, v.Exclusion, v.CentreHabituelId);
}
