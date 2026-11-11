using Pharma.Api.Securite;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Notifications;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Compte super admin (hors de la base) : connexion, puis code de validation avant chaque action sensible.
/// Ces routes ne lisent pas la base : elles marchent même si elle est détruite (le code part alors avec la copie des réglages).
/// </summary>
internal static class SuperAdminEndpoints
{
    public static void MapSuperAdmin(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/super-admin/connexion", (ConnexionRequete req, SeConnecterSuperAdmin connecter, JetonService jetons, ILoggerFactory journaux) =>
        {
            var journal = journaux.CreateLogger("Pharma.SuperAdmin");
            if (connecter.Executer(req.Identifiant, req.MotDePasse) is not { } compte)
            {
                journal.LogWarning("Super admin : connexion refusée ({Identifiant})", req.Identifiant);
                return Results.Json(new ErreurDto("Identifiant ou mot de passe incorrect."), statusCode: StatusCodes.Status401Unauthorized);
            }
            journal.LogWarning("Super admin {Identifiant} connecté", compte.Identifiant);
            var (jeton, expire) = jetons.CreerSuperAdmin(compte.Identifiant);
            return Results.Ok(new ConnexionReponse(jeton, $"Super admin ({compte.Identifiant})", expire));
        });

        var super = app.MapGroup("/api/super-admin").ExigerSuperAdmin();

        super.MapGet("/compte", (ICompteSuperAdmin comptes) =>
            comptes.Lire() is { } c
                ? Results.Ok(new CompteSuperAdminDto(c.Identifiant, RegleDestinataire.MasquerTelephone(c.Telephone), RegleDestinataire.MasquerEmail(c.Email)))
                : Results.NotFound(new ErreurDto("Compte super admin introuvable sur le serveur.")));

        // Avant chaque action sensible : mot de passe ressaisi, puis code envoyé par SMS (e-mail en secours)
        super.MapPost("/validations", async (ValidationSuperAdminRequete req, DemanderValidationSuperAdmin demander, ILoggerFactory journaux, CancellationToken ct) =>
        {
            var d = await demander.ExecuterAsync(req.Action, req.MotDePasse, ct);
            journaux.CreateLogger("Pharma.SuperAdmin").LogWarning("Super admin : code demandé pour {Action}, envoyé par {Canal} au {Destinataire}", req.Action, d.Canal, d.Destinataire);
            return Results.Ok(new DemandeValidationDto(d.Id, d.Canal, d.Destinataire, d.ExpireLe));
        });
    }
}
