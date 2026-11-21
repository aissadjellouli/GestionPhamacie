using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Administration;
using Pharma.Application.Securite;
using Pharma.Application.Synchronisation;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Barre d'état du poste : caisse du poste et dernière sauvegarde (le nom de la base est dans Administration › Sauvegardes) ;
/// compteurs des pastilles (ventes en attente, Chifa, traites, produits revenus, journaux) ; message du titulaire à distance (S7.3).</summary>
internal static class EtatPosteEndpoints
{
    public static void MapEtatPoste(this RouteGroupBuilder api)
    {
        api.MapGet("/etat-poste", async (ObtenirEtatPoste obtenir, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(requete.Poste(), ct);
                return Results.Ok(new EtatPosteDto(e.SauvegardeActive, e.DerniereSauvegarde, e.StatutSauvegarde, e.CaisseSessionId, e.CaisseOuverteLe));
            })
           .ExigerConnexion(motDePasseProvisoire: true, superAdmin: true);

        // Barre d'état (2026-11-21) : compteurs des pastilles en une seule lecture, tout employé connecté (aucun droit d'administration) ;
        // chaque compteur filtré selon les droits de l'employé, gardé 30 secondes au serveur
        api.MapGet("/etat-poste/compteurs", async (ConsulterCompteursBarre consulter, ClaimsPrincipal user, HttpRequest requete, CancellationToken ct) =>
            {
                var c = await consulter.ExecuterAsync(user.UtilisateurId(), requete.Poste(), ct);
                return Results.Ok(new CompteursBarreDto(c.VentesEnAttente, c.ProduitsDisponibles, c.VoletsChifa, c.ChifaADeposer, c.TraitesAPayer,
                    c.JournauxEnAnomalie));
            })
           .ExigerConnexion();

        // Reste du lot S7.3 : message du titulaire à distance (barre d'état de TOUS les employés connectés) ; null : rien à afficher
        api.MapGet("/etat-poste/message-central", async (LireMessageCentral lire, IPermissionService droits, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var m = await lire.ExecuterAsync(ct);
                return Results.Ok(new MessageCentralEtatDto(m is null ? null
                    : new MessageCentralDto(m.Id, m.Texte, m.JusquAuUtc, m.RecuLeUtc, await droits.EstTitulaireAsync(user.UtilisateurId(), ct))));
            })
           .ExigerConnexion();

        // Fermeture par le titulaire de l'officine (403 pour un autre employé) : le message disparaît de tous les postes
        api.MapPost("/etat-poste/message-central/{id:guid}/fermer", async (Guid id, FermerMessageCentral fermer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await fermer.ExecuterAsync(id, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerConnexion();
    }
}
