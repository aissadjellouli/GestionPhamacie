using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Referentiels;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Motifs et catégories réglables (script 094) : motifs actifs proposés à la saisie (tout employé connecté) ;
/// tableau de gestion, ajout, libellé, désactivation (droit admin.parametres). Jamais de suppression.
/// </summary>
internal static class MotifsReglablesEndpoints
{
    public static void MapMotifsReglables(this RouteGroupBuilder api)
    {
        api.MapGet("/motifs/{liste}", async (string liste, MotifsActifs actifs, CancellationToken ct) =>
                Results.Ok((await actifs.ExecuterAsync(liste, ct)).Select(Dto)))
           .ExigerConnexion();

        api.MapGet("/motifs", async (string? liste, string? etat, string? q, int? page, int? taille, ListerMotifsReglables lister,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await lister.ExecuterAsync(liste, etat, q, DemandePage.Lire(page, taille), user.UtilisateurId(), ct);
                return Results.Ok(new PageDto<MotifReglableDto>(p.Lignes.Select(Dto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerPermission(Permissions.AdminParametres)
           .AvecEtatReglageCentral(Pharma.Domain.Reglages.TablesMutualisables.Motifs);

        api.MapPost("/motifs", async (SaisieMotifReglableDto d, EnregistrerMotifReglable enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(null, d.Liste, d.Libelle, true, user.UtilisateurId(), ct, d.Effet, d.Detail))))
           .ExigerPermission(Permissions.AdminParametres);

        api.MapPut("/motifs/{id:long}", async (long id, SaisieMotifReglableDto d, EnregistrerMotifReglable enregistrer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(new IdentifiantDto(await enregistrer.ExecuterAsync(id, d.Liste, d.Libelle, d.Actif, user.UtilisateurId(), ct))))
           .ExigerPermission(Permissions.AdminParametres);
    }

    private static MotifReglableDto Dto(MotifReglable m) => new(m.Id, m.Liste, m.Code, m.Libelle, m.Actif, m.Systeme, m.Effet, m.Detail, m.ModeleCodeCentral);
}
