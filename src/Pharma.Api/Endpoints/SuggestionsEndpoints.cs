using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Suggestions pendant la frappe (décision du titulaire, 2026-11-18) : valeurs déjà saisies d'un champ libre. Le poste n'envoie que la clé
/// du champ ; table, colonne et droit exigé sont dans la liste blanche du serveur (ChampsSuggestions). Clé inconnue : 422 ; droit : 403.
/// </summary>
internal static class SuggestionsEndpoints
{
    public static void MapSuggestions(this RouteGroupBuilder api)
    {
        api.MapGet("/suggestions/{champ}", async (string champ, string? q, ProposerValeurs proposer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(await proposer.ExecuterAsync(champ, q, user.UtilisateurId(), ct)))
           .ExigerConnexion();
    }
}
