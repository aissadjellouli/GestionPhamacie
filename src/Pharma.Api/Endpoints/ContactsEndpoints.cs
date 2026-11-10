using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Contacts;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Fiche contact d'un tiers (patient, grossiste, organisme, client, employé), ouverte depuis toute ligne qui le concerne.</summary>
internal static class ContactsEndpoints
{
    public static void MapContacts(this RouteGroupBuilder api)
    {
        // Connexion exigée ; le droit propre à chaque type de tiers est vérifié par ConsulterContact
        api.MapGet("/contacts/{type}/{id:long}", async (string type, long id, ConsulterContact consulter, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var f = await consulter.ExecuterAsync(type, id, user.UtilisateurId(), ct);
                return Results.Ok(new FicheContactDto(f.Type, f.Id, f.Nom, f.Precision, f.Actif,
                    f.Coordonnees.Select(i => new InfoContactDto(i.Libelle, i.Valeur, i.Genre)).ToList(),
                    f.Situation.Select(i => new InfoContactDto(i.Libelle, i.Valeur, i.Genre)).ToList(), f.CoordonneesMasquees));
            })
           .RequireAuthorization();
    }
}
