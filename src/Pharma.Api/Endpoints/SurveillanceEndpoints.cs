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
                await signaler.ExecuterAsync(req.Type, req.ProduitId, req.LotId, req.Quantite, req.Montant, user.UtilisateurId(), requete.Poste(), ct,
                    req.MotifCode, req.MotifTexte);
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
                    p.SeuilRemisesPatient, p.HeureOuverture, p.HeureFermeture, p.SeuilAjustements, p.SeuilCasses, p.SeuilEchantillons, p.JoursComptageSensibles,
                    p.ReceptionAveugle, p.JoursAvoirRetour, p.SeuilCreditNouveauPatient, p.JoursPatientRecent, p.SeuilCreditsJour,
                    p.JoursCreditSansRemboursement, p.SeuilAutorisationsValideur, p.SeuilAutorisationsEmploye, p.MinutesPostesSimultanes,
                    p.SeuilChangementsPoste, p.SeuilBaissesPrix));
            });

        surveillance.MapPut("/parametres", async (ParametresSurveillanceDto req, ReglerSurveillance regler, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await regler.EnregistrerAsync(new ParametresSurveillance(req.SeuilLignesRetirees, req.SeuilTiroir, req.SeuilAttentesAbandonnees,
                    req.SeuilRemises, req.SeuilRemisesPatient, req.HeureOuverture, req.HeureFermeture, req.SeuilAjustements, req.SeuilCasses,
                    req.SeuilEchantillons, req.JoursComptageSensibles, req.ReceptionAveugle, req.JoursAvoirRetour,
                    req.SeuilCreditNouveauPatient, req.JoursPatientRecent, req.SeuilCreditsJour, req.JoursCreditSansRemboursement,
                    req.SeuilAutorisationsValideur, req.SeuilAutorisationsEmploye, req.MinutesPostesSimultanes, req.SeuilChangementsPoste,
                    req.SeuilBaissesPrix), user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Produits sensibles : comptés à part et régulièrement
        surveillance.MapGet("/sensibles", async (GererProduitsSensibles gerer, ClaimsPrincipal user, CancellationToken ct) =>
            Results.Ok((await gerer.ListerAsync(user.UtilisateurId(), ct)).Select(p => new ProduitSensibleDto(p.ProduitId, p.Nom, p.Stock))));

        surveillance.MapPut("/sensibles/{produitId:long}", async (long produitId, MarquerSensibleRequete req, GererProduitsSensibles gerer,
                ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.MarquerAsync(produitId, req.Sensible, user.UtilisateurId(), ct);
                return Results.NoContent();
            });

        // Écarts dans le temps (par jour, poste, employés présents) et risques par employé
        surveillance.MapGet("/risques", async (DateOnly? du, DateOnly? au, ObtenirTableauRisques obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var t = await obtenir.ExecuterAsync(du, au, user.UtilisateurId(), ct);
                var noms = t.Noms;
                return Results.Ok(new TableauRisquesDto(t.Du, t.Au,
                    t.Jours.Select(j => new JourEcartsDto(j.Jour, j.Ecart, j.Manque, j.PerteStock, j.BoitesAjustees, j.ValeurFantomes, j.Anomalies,
                        j.Urgentes, j.Presents.Select(id => noms.GetValueOrDefault(id, $"Employé {id}")).Order().ToList(), j.Postes)).ToList(),
                    t.Employes.Select(e => new RisqueEmployeDto(e.Activite.UtilisateurId, e.Activite.Nom, e.Activite.Actif, e.Presence.JoursPresent,
                        e.Presence.JoursAvecPerte, e.Presence.PerteEnPresence, e.Presence.MoyennePresent, e.Presence.MoyenneAbsent, e.Presence.Signale,
                        e.Activite.ManqueCaisse, e.Activite.Clotures, e.Activite.Anomalies, e.Activite.Urgentes, e.Activite.Annulations,
                        e.Activite.RetoursClients, e.Activite.Remises, e.Activite.Credits, e.Activite.AutorisationsDemandees, e.Activite.AutorisationsDonnees,
                        e.Activite.Casses, e.Activite.Echantillons, e.Activite.BoitesAjustees)).ToList(),
                    Pharma.Domain.Rapports.RegleRisques.JoursPerteMin, Pharma.Domain.Rapports.RegleRisques.Facteur, Pharma.Domain.Rapports.RegleRisques.JoursAbsentMin));
            });
    }
}
