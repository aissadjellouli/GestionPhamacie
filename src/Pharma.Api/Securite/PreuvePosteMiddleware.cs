using System.Collections.Concurrent;
using Pharma.Application.Notifications;
using Pharma.Application.Postes;
using Pharma.Shared;

namespace Pharma.Api.Securite;

/// <summary>
/// Preuve de poste à chaque requête (contrat § 3) : après les barrages, AVANT l'authentification. Mode « inactif » : rien. Sinon, en-têtes de preuve présents →
/// vérification (valide : poste prouvé posé dans HttpContext.Items ; révoqué ou horloge : 401 ; vérification impossible : 503 ; fausse ou inconnue : 401 en mode
/// « exige », traitée comme absente en mode « transition ») ; sans preuve valide, un en-tête X-Pharma-Poste → 403 en mode « exige », ou si le nom est réservé à
/// un poste enrôlé (mode « transition », D6). Routes ouvertes (§ 3 bis) : jamais refusées.
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

    /// <summary>Mode lu pour cette requête (inactif si la liste n'a pas été consultée).</summary>
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
        var mode = await registre.ModeAsync(ctx.RequestAborted);
        ctx.Items[ItemMode] = mode;
        if (mode == PreuvePoste.ModeInactif)
        {
            await suite(ctx);
            return;
        }
        var ouverte = RouteOuverte(ctx.Request.Path);
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
            // Mode « transition » : une preuve fausse ou inconnue (poste enrôlé sur une autre base, clé perdue) vaut absence de preuve : le poste continue
            // comme un poste non autorisé (bandeau, nom réservé refusé, jamais compté pour la bascule). Révoqué, horloge, vérification impossible : refusés.
            if (!ouverte && !(mode == PreuvePoste.ModeTransition && r.Issue == IssuePreuve.NonProuve))
            {
                await RefuserAsync(ctx, r.Issue == IssuePreuve.Indisponible ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status401Unauthorized, r.Code!);
                return;
            }
        }
        if (!ctx.Items.ContainsKey(PorteeConnexion.ItemPosteProuve) && !ouverte && PorteeConnexion.PosteAnnonceParEnTete(ctx.Request) is { } annonce
                 && (mode == PreuvePoste.ModeExige || await registre.NomReserveAsync(annonce, ctx.RequestAborted)))
        {
            await RefuserAsync(ctx, StatusCodes.Status403Forbidden, PreuvePoste.PosteNonAutorise);
            return;
        }
        await suite(ctx);
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
/// Contrôle du jeton face au poste prouvé (contrat § 3), après l'authentification ; remplace la règle « en-tête différent du claim ». Mode « inactif » : règle
/// d'avant (403 si l'en-tête annonce un autre poste que le jeton). Sinon : claim « poste_id » = poste prouvé, ou 401 ; jeton lié à un nom sans « poste_id » :
/// refusé en mode « exige », et en mode « transition » si la requête n'est pas prouvée et que l'en-tête diffère ou que ce nom est réservé à un poste enrôlé.
/// Routes ouvertes : jamais refusées.
/// </summary>
public static class ControlePosteJeton
{
    public static async Task FiltrerAsync(HttpContext ctx, Func<Task> suite)
    {
        var mode = PreuvePosteMiddleware.Mode(ctx);
        if (mode == PreuvePoste.ModeInactif)
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
        bool refus;
        if (claimId is not null)
            refus = prouve is null || !string.Equals(claimId, prouve.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        else if (!string.IsNullOrWhiteSpace(claimNom))
            refus = mode == PreuvePoste.ModeExige
                    // Jeton d'un autre poste présenté par un poste prouvé (jeton recopié)
                    || (prouve is not null && !string.Equals(claimNom.Trim(), prouve.Nom, StringComparison.OrdinalIgnoreCase))
                    || (prouve is null && (PorteeConnexion.PosteDiffere(ctx)
                        || await ctx.RequestServices.GetRequiredService<RegistrePostesAutorises>().NomReserveAsync(claimNom, ctx.RequestAborted)));
        else refus = false;
        if (refus)
        {
            await PreuvePosteMiddleware.RefuserAsync(ctx, StatusCodes.Status401Unauthorized, PreuvePoste.PosteNonProuve);
            return;
        }
        await suite();
    }
}
