using System.Security.Claims;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Postes;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Liste des postes autorisés (docs/taches/2026-11-24-postes-autorises/contrat-api.md § 4 et § 5) : état du poste appelant et enrôlement (routes ouvertes),
/// gestion par le titulaire seul (Administration › Postes).
/// </summary>
internal static class PostesEndpoints
{
    public static void MapPostes(this RouteGroupBuilder api)
    {
        // État du poste appelant : mode de la liste et preuve jointe (valide ou refusée), sans rien révéler d'autre
        api.MapGet("/postes/etat", async (HttpContext http, RegistrePostesAutorises registre, CancellationToken ct) =>
        {
            var mode = await registre.ModeAsync(ct);
            var prouve = http.PosteProuve();
            return Results.Ok(new EtatPosteAutoriseDto(mode, prouve is not null, prouve?.Id, prouve?.Nom, PreuvePosteMiddleware.Refus(http)));
        }).AllowAnonymous();

        // Enrôlement par code (limite de débit des connexions : ReseauSecurise.RoutesConnexion)
        api.MapPost("/postes/enrolement", async (EnrolementPosteRequete? req, EnrolerPoste enroler, CancellationToken ct) =>
                Results.Ok(await enroler.ExecuterAsync(req, ct)))
           .AllowAnonymous()
           .GererRefusPostes();

        var admin = api.MapGroup("/admin/postes").GererRefusPostes();

        admin.MapGet("", async (string? statut, string? recherche, int? page, int? taille, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var p = await gerer.ListerAsync(user.UtilisateurId(), statut, recherche, DemandePage.Lire(page, taille), ct);
                return Results.Ok(new PageDto<PosteAutoriseDto>(p.Lignes.Select(VersDto).ToList(), p.Total, p.Numero, p.Taille));
            })
           .ExigerTitulaire();

        admin.MapGet("/reglages", async (GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                var (r, noms) = await gerer.ReglagesAsync(user.UtilisateurId(), ct);
                return Results.Ok(new ReglagesPostesDto(r.Mode, r.MaximumPostes, r.DureeCodeMinutes, noms.Select(n => new NomSansPreuveDto(n.Nom, n.VuLe)).ToList()));
            })
           .ExigerTitulaire();

        admin.MapPut("/reglages", async (ReglagesPostesDto req, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.ModifierReglagesAsync(user.UtilisateurId(), new ReglagesPostes(req.Mode, req.MaximumPostes, req.DureeCodeMinutes), ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        admin.MapPost("", async (NomPosteRequete req, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await gerer.AutoriserAsync(user.UtilisateurId(), req.Nom, ct))))
           .ExigerTitulaire();

        admin.MapPost("/{id:long}/code", async (long id, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await gerer.NouveauCodeAsync(user.UtilisateurId(), id, ct))))
           .ExigerTitulaire();

        admin.MapPost("/{id:long}/remplacer", async (long id, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
                Results.Ok(VersDto(await gerer.RemplacerAsync(user.UtilisateurId(), id, ct))))
           .ExigerTitulaire();

        admin.MapPut("/{id:long}/nom", async (long id, NomPosteRequete req, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.RenommerAsync(user.UtilisateurId(), id, req.Nom, ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();

        admin.MapPost("/{id:long}/revoquer", async (long id, RevocationPosteRequete req, GererPostesAutorises gerer, ClaimsPrincipal user, CancellationToken ct) =>
            {
                await gerer.RevoquerAsync(user.UtilisateurId(), id, req.Motif, ct);
                return Results.NoContent();
            })
           .ExigerTitulaire();
    }

    private static PosteAutoriseDto VersDto(PosteAutoriseLu p) => new(p.Id, p.Nom, p.Statut, p.Generation, p.CreeLe, p.EnroleLe, p.DernierePreuveLe,
        p.Ordinateur, p.EmpreinteCle?.Trim(), p.CodeExpireLe, p.RevoqueLe, p.MotifRevocation);

    private static CodeEnrolementDto VersDto(CodeEnrolementCree c) => new(c.PosteId, c.Nom, c.Code, c.ExpireLe);

    /// <summary>Refus sur les postes (RefusPosteException) rendus en ProblemDetails : type urn:pharmaofficine:erreur:&lt;code&gt;, extension « code ».</summary>
    private static TBuilder GererRefusPostes<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (RefusPosteException e)
            {
                return Results.Problem(title: "Refusé", detail: e.Message, statusCode: e.Statut, type: $"urn:pharmaofficine:erreur:{e.Code}",
                    extensions: new Dictionary<string, object?> { ["code"] = e.Code });
            }
        });
        return builder;
    }
}
