using Pharma.Api.Securite;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Notifications;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Compte super admin (hors de la base) : connexion, puis code de validation avant chaque action sensible.
/// Ces routes ne lisent pas la base : elles marchent même si elle est détruite (le code part alors avec la copie des réglages).
/// </summary>
public static class SuperAdminEndpoints
{
    /// <summary>Code super admin demandé : action et canaux réellement utilisés au journal, jamais le code ni le destinataire (téléphone ou e-mail).</summary>
    public static void JournaliserCodeDemande(ILogger journal, string action, string canal) =>
        journal.LogWarning("Super admin : code demandé pour {Action}, envoyé par {Canal}", action, canal);

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
                ? Results.Ok(new CompteSuperAdminDto(c.Identifiant, c.ASms ? RegleDestinataire.MasquerTelephone(c.Telephone!) : null, c.AEmail ? RegleDestinataire.MasquerEmail(c.Email!) : null))
                : Results.NotFound(new ErreurDto("Compte super admin introuvable sur le serveur.")));

        // Avant chaque action sensible : mot de passe ressaisi, puis le même code envoyé par SMS et par e-mail (un canal suffit)
        super.MapPost("/validations", async (ValidationSuperAdminRequete req, DemanderValidationSuperAdmin demander, ILoggerFactory journaux, CancellationToken ct) =>
        {
            var d = await demander.ExecuterAsync(req.Action, req.MotDePasse, ct);
            JournaliserCodeDemande(journaux.CreateLogger("Pharma.SuperAdmin"), req.Action, d.Canal);
            return Results.Ok(new DemandeValidationDto(d.Id, d.Canal, d.Destinataire, d.ExpireLe, d.Message));
        });

        // ---- Restauration : sauvegardes proposées, puis restauration en tâche de fond (le poste suit les étapes) ----
        super.MapGet("/restauration", async (ListerRestaurations lister, CancellationToken ct) =>
        {
            var c = await lister.ExecuterAsync(ct);
            return Results.Ok(new ChoixRestaurationDto(c.NomBase, c.BaseExiste, c.DossierExterne, c.DisqueBranche, c.SourceGed,
                c.Sauvegardes.Select(s => new SauvegardeDisponibleDto(s.Fichier, s.Emplacement, s.Date, s.Taille)).ToList()));
        });

        super.MapGet("/restauration/etat", (RestaurationsSuperAdmin restaurations) => Results.Ok(VersDto(restaurations.Etat)));

        super.MapPost("/restauration/base", async (RestaurationBaseRequete req, RestaurationsSuperAdmin restaurations, SuperAdminCourant courant, CancellationToken ct) =>
                Results.Ok(VersDto(await restaurations.RestaurerBaseAsync(req.Fichier, courant, ct))))
            .ValidationSuperAdmin(ActionsSuperAdmin.RestaurerBase);

        super.MapPost("/restauration/ged", async (RestaurationsSuperAdmin restaurations, SuperAdminCourant courant, CancellationToken ct) =>
                Results.Ok(VersDto(await restaurations.RestaurerGedAsync(courant, ct))))
            .ValidationSuperAdmin(ActionsSuperAdmin.RestaurerGed);
    }

    private static EtatRestaurationDto VersDto(EtatRestauration e) => new(e.EnCours, e.Type, e.Debut, e.Fin, e.Etapes.ToList(), e.Resultat, e.Erreur);
}
