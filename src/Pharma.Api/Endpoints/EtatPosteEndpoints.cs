using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Administration;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Barre d'état du poste : caisse du poste et dernière sauvegarde (le nom de la base est dans Administration › Sauvegardes) ;
/// compteurs des pastilles (ventes en attente, Chifa, traites, produits revenus, journaux).</summary>
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
    }
}
