using Pharma.Api.Securite;
using Pharma.Application.Administration;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Barre d'état du poste : caisse du poste et dernière sauvegarde (le nom de la base est dans Administration › Sauvegardes).</summary>
internal static class EtatPosteEndpoints
{
    public static void MapEtatPoste(this RouteGroupBuilder api)
    {
        api.MapGet("/etat-poste", async (ObtenirEtatPoste obtenir, HttpRequest requete, CancellationToken ct) =>
            {
                var e = await obtenir.ExecuterAsync(requete.Poste(), ct);
                return Results.Ok(new EtatPosteDto(e.SauvegardeActive, e.DerniereSauvegarde, e.StatutSauvegarde, e.CaisseSessionId, e.CaisseOuverteLe));
            })
           .RequireAuthorization();
    }
}
