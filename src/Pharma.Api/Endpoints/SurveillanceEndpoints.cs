using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Surveillance;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Surveillance de la caisse : le poste signale ce qui ne laisse pas de vente (ligne retirée du panier, tiroir ouvert) ;
/// le titulaire consulte et traite les anomalies, et règle les seuils.
/// </summary>
internal static class SurveillanceEndpoints
{
    public static void MapSurveillance(this RouteGroupBuilder api)
    {
        // Tout employé de caisse ; le tiroir demande en plus le droit « caisse.ouvrir_tiroir » (contrôlé par le cas d'usage)
        api.MapPost("/caisse/evenements", async (EvenementCaisseRequete req, SignalerEvenementCaisse signaler, ClaimsPrincipal user, HttpRequest requete,
                CancellationToken ct) =>
            {
                await signaler.ExecuterAsync(req.Type, req.ProduitId, req.LotId, req.Quantite, req.Montant, user.UtilisateurId(), requete.Poste(), ct);
                return Results.NoContent();
            })
           .ExigerPermission(Permissions.CaisseVendre);

        var surveillance = api.MapGroup("/surveillance").ExigerTitulaire();

        surveillance.MapGet("", async (bool? traitees, ListerSurveillance lister, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await lister.ExecuterAsync(traitees == true, user.UtilisateurId(), ct)).Select(a => new AlerteSurveillanceDto(
                a.Id, a.Type, a.Niveau, a.UtilisateurId, a.Employe, a.Jour, a.Message, a.Valeur, a.Statut, a.MiseAJourLe, a.TraiteePar, a.TraiteeLe, a.ActionPrise))));

        surveillance.MapPost("/{id:long}/traiter", async (long id, TraiterAlerteRequete req, TraiterSurveillance traiter, ClaimsPrincipal user, CancellationToken ct) =>
            await traiter.ExecuterAsync(id, req.ActionPrise, user.UtilisateurId(), ct)
                ? Results.NoContent()
                : Results.NotFound(new ErreurDto("Anomalie introuvable ou déjà traitée : actualisez la liste.")));

        surveillance.MapGet("/parametres", async (ReglerSurveillance regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await regler.LireAsync(user.UtilisateurId(), ct);
                return Results.Ok(new ParametresSurveillanceDto(p.SeuilLignesRetirees, p.SeuilTiroir, p.SeuilAttentesAbandonnees, p.SeuilRemises,
                    p.SeuilRemisesPatient, p.HeureOuverture, p.HeureFermeture));
            });

        surveillance.MapPut("/parametres", async (ParametresSurveillanceDto req, ReglerSurveillance regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await regler.EnregistrerAsync(new ParametresSurveillance(req.SeuilLignesRetirees, req.SeuilTiroir, req.SeuilAttentesAbandonnees,
                    req.SeuilRemises, req.SeuilRemisesPatient, req.HeureOuverture, req.HeureFermeture), user.UtilisateurId(), ct);
                return Results.NoContent();
            });
    }
}
