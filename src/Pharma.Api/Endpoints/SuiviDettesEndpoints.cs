using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Suivi;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>Dettes et créances de tous les tiers : échéancier et alertes (droit « tresorerie.gerer ») ; délai d'alerte réglé par le titulaire.</summary>
internal static class SuiviDettesEndpoints
{
    public static void MapSuiviDettes(this RouteGroupBuilder api)
    {
        api.MapGet("/suivi-dettes", async (ObtenirSuiviDettes obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var s = await obtenir.ExecuterAsync(user.UtilisateurId(), ct);
                return Results.Ok(new SuiviDettesDto(s.DelaiJours, s.Jour, s.TotalCreances, s.TotalDettes,
                    s.Lignes.Select(Ligne).ToList(),
                    s.Tranches.Select(t => new TotalTrancheDto(t.Sens, t.Tranche, t.Nombre, t.Montant)).ToList(),
                    s.Alertes.Select(a => new AlerteDetteDto(a.Sens, a.Categorie, a.Nombre, a.Montant, a.Message)).ToList()));
            })
           .ExigerPermission(Permissions.TresorerieGerer);

        // Liste paginée et filtrée par le serveur (catégorie, tiers, état, période) : total de tout le filtre, tiers de la catégorie
        api.MapGet("/suivi-dettes/lignes", async (string? categorie, string? tiers, string? etat, DateOnly? du, DateOnly? au, string? recherche, int? page, int? taille,
                ObtenirSuiviDettes obtenir, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var r = await obtenir.LignesAsync(user.UtilisateurId(), FiltreSuiviDettes.Lire(categorie, tiers, etat, du, au, recherche), DemandePage.Lire(page, taille), ct);
                var p = r.Page.Vers(Ligne);
                return Results.Ok(new SuiviDettesLignesDto(new PageDto<LigneSuiviDto>(p.Lignes, p.Total, p.Numero, p.Taille), r.Total,
                    r.Tiers.Select(t => new TiersSuiviDto(t.Valeur, t.Nom)).ToList()));
            })
           .ExigerPermission(Permissions.TresorerieGerer);

        api.MapPut("/suivi-dettes/delai", async (DelaiAlerteDettesRequete req, ModifierDelaiAlerteDettes modifier, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await modifier.ExecuterAsync(req.Jours, user.UtilisateurId(), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
    }

    private static LigneSuiviDto Ligne(LigneSuivi l) => new(l.Somme.Sens, l.Somme.Categorie, l.Somme.Tiers, l.Somme.Libelle, l.Somme.Depuis, l.Somme.Echeance,
        l.Somme.Montant, l.Somme.Rejetee, l.Jours, l.Tranche, l.EnAlerte, l.Somme.TiersId);
}
