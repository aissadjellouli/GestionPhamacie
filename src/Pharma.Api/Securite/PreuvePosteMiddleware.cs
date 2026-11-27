using System.Collections.Concurrent;
using Pharma.Application.Notifications;
using Pharma.Application.Postes;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Preuve de poste à chaque requête (contrat § 3) : après les barrages, AVANT l'authentification. Preuve toujours exigée (décision du titulaire du
/// 2026-11-27, script 174 : les modes « inactif » et « transition » ne s'appliquent plus). En-têtes de preuve présents → vérification (valide : poste
/// prouvé posé dans HttpContext.Items ; révoqué, horloge, fausse ou inconnue : 401 ; vérification impossible : 503) ; sans preuve valide, un en-tête
/// X-Pharma-Poste (un PC de caisse) → 403 « poste_non_autorise ». Clients sans poste (ni en-tête X-Pharma-Poste ni preuve : page mobile, pilotage
/// mobile, outils) : inchangés. Routes ouvertes (§ 3 bis) et routes du super admin : jamais refusées (la preuve y est seulement lue).
/// PC non prouvé (sans preuve, ou preuve d'un poste révoqué, remplacé ou inconnu) : le titulaire y garde l'Administration seulement (décision du
/// 2026-11-27, <see cref="AccesAdministrationSeule"/>) ; le refus se décide alors après la lecture du jeton.
/// </summary>
public sealed class PreuvePosteMiddleware(RequestDelegate suite, ILogger<PreuvePosteMiddleware> journal)
{
    public const string ItemMode = "Pharma.ModePostes";
    public const string ItemRefus = "Pharma.RefusPreuvePoste";
    private readonly ConcurrentDictionary<string, long> journalRefus = new(StringComparer.Ordinal);

    /// <summary>Routes où la preuve est seulement lue, jamais exigée (contrat § 3 bis).</summary>
    public static bool RouteOuverte(PathString chemin) =>
        !chemin.StartsWithSegments("/api") || chemin.StartsWithSegments("/api/healthcheck") || chemin.StartsWithSegments("/api/postes/etat")
        || chemin.StartsWithSegments("/api/postes/enrolement") || chemin.StartsWithSegments("/api/inter-serveurs") || chemin.StartsWithSegments("/api/vigie/rapport");

    /// <summary>
    /// Routes du super admin (connexion, restauration, reprise de données) : jamais refusées par la preuve, pour que le super admin garde son accès depuis
    /// n'importe quel PC, même avant toute autorisation (restauration sur un serveur neuf). Elles restent protégées par le jeton du super admin
    /// (ExigerSuperAdmin) et par le code reçu pour chaque action sensible ; un jeton d'employé y est refusé.
    /// </summary>
    public static bool RouteSuperAdmin(PathString chemin) =>
        chemin.StartsWithSegments("/api/super-admin") || chemin.StartsWithSegments("/api/admin/reprise");

    /// <summary>Mode lu pour cette requête (inactif si l'intergiciel n'est pas passé : hors /api ou sans liste des postes).</summary>
    public static string Mode(HttpContext ctx) => ctx.Items.TryGetValue(ItemMode, out var m) && m is string s ? s : PreuvePoste.ModeInactif;

    /// <summary>Code de la preuve refusée sur une route ouverte (null : pas de preuve ou preuve valide).</summary>
    public static string? Refus(HttpContext ctx) => ctx.Items.TryGetValue(ItemRefus, out var r) ? r as string : null;

    /// <summary>En-têtes de preuve de la requête (plusieurs valeurs pour un en-tête : traité comme mal formé).</summary>
    public static EnTetesPreuve EnTetes(IHeaderDictionary h) =>
        new(Un(h, PreuvePoste.EnTeteId), Un(h, PreuvePoste.EnTeteHorodatage), Un(h, PreuvePoste.EnTeteNonce), Un(h, PreuvePoste.EnTetePreuve));

    private static string? Un(IHeaderDictionary h, string nom) => h.TryGetValue(nom, out var v) ? (v.Count == 1 ? v[0] ?? "" : "") : null;

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!ctx.Request.Path.StartsWithSegments("/api") || ctx.RequestServices.GetService<RegistrePostesAutorises>() is not { } registre)
        {
            await suite(ctx);
            return;
        }
        ctx.Items[ItemMode] = await registre.ModeAsync(ctx.RequestAborted);   // toujours « exige » (script 174)
        var ouverte = RouteOuverte(ctx.Request.Path) || RouteSuperAdmin(ctx.Request.Path);
        var entetes = EnTetes(ctx.Request.Headers);
        if (entetes.Presents)
        {
            var verif = ctx.RequestServices.GetRequiredService<VerificateurPreuvePoste>();
            var r = await verif.VerifierAsync(entetes, ctx.Request.Method, InterServeursMiddleware.CibleBrute(ctx), ctx.Request.Headers.Authorization.FirstOrDefault(),
                ctx.RequestAborted);
            if (r is { Issue: IssuePreuve.Valide, Poste: { } p })
            {
                ctx.Items[PorteeConnexion.ItemPosteProuve] = p;
                ctx.RequestServices.GetService<DernieresPreuvesPostes>()?.Noter(p.Id);
                await suite(ctx);
                return;
            }
            ctx.Items[ItemRefus] = r.Code;
            Noter(ctx, r);
            if (!ouverte)
            {
                var statut = r.Issue == IssuePreuve.Indisponible ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized;
                // Poste révoqué, remplacé ou inconnu : PC non prouvé, le titulaire y garde l'Administration (horloge et vérification impossible : refus)
                if (r.Issue is IssuePreuve.Revoque or IssuePreuve.NonProuve && await PcNonProuveAsync(ctx, statut, r.Code!)) return;
                await RefuserAsync(ctx, statut, r.Code!);
                return;
            }
        }
        // PC de caisse (il s'annonce par X-Pharma-Poste) sans preuve valide : refusé, quel que soit son nom (titulaire : Administration seulement)
        if (!ctx.Items.ContainsKey(PorteeConnexion.ItemPosteProuve) && !ouverte && PorteeConnexion.PosteAnnonceParEnTete(ctx.Request) is not null)
        {
            if (await PcNonProuveAsync(ctx, StatusCodes.Status403Forbidden, PreuvePoste.PosteNonAutorise)) return;
            await RefuserAsync(ctx, StatusCodes.Status403Forbidden, PreuvePoste.PosteNonAutorise);
            return;
        }
        await suite(ctx);
    }

    /// <summary>
    /// PC non prouvé (décision du titulaire du 2026-11-27 : titulaire, Administration seulement). Le jeton n'est pas encore lu ici : la requête
    /// passe, avec son refus d'origine, jusqu'au contrôle du jeton (AccesAdministrationSeule.FiltrerAsync) si elle vise une route de la liste
    /// fermée, ou si elle porte un jeton (un jeton marqué y reçoit 403 « administration_seule » au lieu d'une déconnexion). Sans jeton, hors de la
    /// liste : refus d'origine tout de suite. Vrai si la requête a été passée à la suite.
    /// </summary>
    private async Task<bool> PcNonProuveAsync(HttpContext ctx, int statut, string code)
    {
        if (!AccesAdministrationSeule.RouteAutorisee(ctx.Request.Path) && string.IsNullOrEmpty(ctx.Request.Headers.Authorization.FirstOrDefault())) return false;
        AccesAdministrationSeule.LaisserPasser(ctx, statut, code);
        await suite(ctx);
        return true;
    }

    /// <summary>Refus en ProblemDetails ; « preuve_horloge » porte l'heure du serveur (le poste corrige son décalage et réessaie une fois).</summary>
    public static Task RefuserAsync(HttpContext ctx, int statut, string code)
    {
        if (code == PreuvePoste.PreuveHorloge)
            ctx.Response.Headers[PreuvePoste.EnTeteHeureServeur] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (titre, detail) = code switch
        {
            PreuvePoste.PreuveHorloge => ("Horloge du poste", "L'heure de ce PC est trop éloignée de celle du serveur : réglez l'horloge du PC."),
            PreuvePoste.PosteRevoque => ("Poste révoqué", "Ce poste a été révoqué ou remplacé par le titulaire : demandez-lui un nouveau code (Administration › Postes)."),
            PreuvePoste.PosteNonAutorise => ("Poste non autorisé",
                "Ce poste n'est pas autorisé : le titulaire doit l'ajouter dans Administration › Postes et vous donner un code."),
            PreuvePoste.PreuveIndisponible => ("Vérification indisponible", "Vérification du poste momentanément impossible : réessayez."),
            AccesAdministrationSeule.Code => ("Administration seulement", AccesAdministrationSeule.Message),
            _ => ("Poste non prouvé", "Ce poste n'a pas pu prouver son identité : reconnectez-vous ; si cela recommence, faites réautoriser le poste.")
        };
        return Results.Problem(title: titre, detail: detail, statusCode: statut, type: $"urn:pharmaofficine:erreur:{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(ctx);
    }

    // Journal du serveur : au plus une ligne par heure, par poste et par adresse ; alerte au titulaire si un poste révoqué se présente
    private void Noter(HttpContext ctx, ResultatPreuve r)
    {
        var adresse = ctx.Connection.RemoteIpAddress?.ToString() ?? "inconnue";
        var cle = $"{r.PosteId}|{adresse}|{r.Issue}";
        var t = Environment.TickCount64;
        if (journalRefus.TryGetValue(cle, out var d) && t - d < 3_600_000) return;
        if (journalRefus.Count > 1000) journalRefus.Clear();
        journalRefus[cle] = t;
        journal.LogWarning("Preuve de poste refusée ({Refus}) pour le poste {PosteId} depuis {Adresse}", r.Code, r.PosteId, adresse);
        if (r.Issue == IssuePreuve.Revoque && r.PosteId is { } id && ctx.RequestServices.GetService<AlertesPostes>() is { } limite
            && limite.Autorisee("revoque:" + id))
        {
            var fabrique = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>();
            _ = Task.Run(async () =>
            {
                try
                {
                    using var portee = fabrique.CreateScope();
                    await portee.ServiceProvider.GetRequiredService<IAlertesTitulaire>().SurveillanceAsync(
                        $"Postes : un poste révoqué (n° {id}) s'est présenté au serveur depuis l'adresse {adresse}.", true);
                }
                catch (Exception e) { journal.LogWarning(e, "Postes : alerte « poste révoqué » non envoyée"); }
            });
        }
    }
}

/// <summary>
/// Contrôle du jeton face au poste prouvé (contrat § 3), après l'authentification ; remplace la règle « en-tête différent du claim ». Preuve toujours
/// exigée (script 174) : claim « poste_id » = poste prouvé, sinon 401 ; jeton lié à un nom sans « poste_id » (ouvert sur un PC non autorisé) : 401.
/// Jeton sans poste (page mobile, super admin) : inchangé. Routes ouvertes : jamais refusées. Jeton marqué « administration seulement » (titulaire
/// connecté sur un PC non autorisé) : routes de l'Administration seulement, sur tout PC (<see cref="AccesAdministrationSeule"/>).
/// </summary>
public static class ControlePosteJeton
{
    public static async Task FiltrerAsync(HttpContext ctx, Func<Task> suite)
    {
        // Titulaire sur un PC non autorisé (décision du 2026-11-27) : jeton marqué limité à l'Administration (sur tout PC, même intergiciel de
        // preuve non passé), PC non prouvé sans jeton marqué refusé comme avant. Routes ouvertes : jamais refusées.
        if (!PreuvePosteMiddleware.RouteOuverte(ctx.Request.Path) && await AccesAdministrationSeule.FiltrerAsync(ctx, suite)) return;
        // Intergiciel de preuve non passé (hors /api, liste des postes absente) : règle d'avant (403 si l'en-tête annonce un autre poste que le jeton)
        if (PreuvePosteMiddleware.Mode(ctx) == PreuvePoste.ModeInactif)
        {
            if (PorteeConnexion.PosteDiffere(ctx))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new ErreurDto("Cette connexion a été ouverte sur un autre poste : reconnectez-vous sur ce poste."));
                return;
            }
            await suite();
            return;
        }
        if (ctx.User.Identity?.IsAuthenticated != true || PreuvePosteMiddleware.RouteOuverte(ctx.Request.Path))
        {
            await suite();
            return;
        }
        var prouve = ctx.PosteProuve();
        var claimId = ctx.User.FindFirst(PorteeConnexion.RevendicationPosteId)?.Value;
        var claimNom = ctx.User.FindFirst(PorteeConnexion.RevendicationPoste)?.Value;
        var refus = claimId is not null
            ? prouve is null || !string.Equals(claimId, prouve.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            : !string.IsNullOrWhiteSpace(claimNom);
        if (refus)
        {
            await PreuvePosteMiddleware.RefuserAsync(ctx, StatusCodes.Status401Unauthorized, PreuvePoste.PosteNonProuve);
            return;
        }
        await suite();
    }
}
