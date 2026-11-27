using System.Net;
using Microsoft.AspNetCore.Http.Features;
using Pharma.Application.Ged;
using Pharma.Application.SecondServeur;
using Pharma.Application.Catalogue;

namespace Pharma.Api.Securite;

// ---- Vague 2, lot V2-5 : relais du 2e serveur (en veille) vers l'API du serveur 1 (plan-vague-2.md § 4, contrat-api.md § 2.6) -------------

/// <summary>
/// Délais d'abandon d'un appel relayé : mêmes valeurs que le poste (Pharma.Desktop/Services/AppelsEnCours.cs, classe DelaisAppels ; test de parité) :
/// 30 s en général, 2 min pour produire un document, 10 min pour les opérations lourdes (dont l'envoi de documents à la GED).
/// </summary>
public static class DelaisRelais
{
    public static readonly TimeSpan Normal = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Document = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Lourd = TimeSpan.FromMinutes(10);

    public static readonly IReadOnlyList<string> Lourds =
        ["/api/admin/sauvegardes/lancer", "/api/admin/sauvegardes/secours/tester", "/api/admin/reprise/", "/api/super-admin/restauration", "/api/admin/nomenclature", "/api/ged/",
         "/api/mobile/reception/scans/importer"];

    public static readonly IReadOnlyList<string> Documents = ["/pdf", "/export", "/etiquettes", "/rapport"];

    public static TimeSpan Pour(string chemin) =>
        Lourds.Any(l => chemin.StartsWith(l, StringComparison.OrdinalIgnoreCase)) ? Lourd
        : Documents.Any(d => chemin.Contains(d, StringComparison.OrdinalIgnoreCase)) ? Document
        : Normal;

    /// <summary>Taille de corps relevée comme sur l'API (les deux seules routes qui la relèvent) ; null = limite par défaut du serveur.</summary>
    public static long? TailleCorpsMax(string chemin) =>
        chemin.StartsWith("/api/ged/", StringComparison.OrdinalIgnoreCase) ? DeposerDocumentGed.TailleMax + 1L
        : string.Equals(chemin.TrimEnd('/'), "/api/admin/nomenclature", StringComparison.OrdinalIgnoreCase) ? ImporterNomenclature.TailleMax
        : null;
}

/// <summary>
/// Relais : le 2e serveur en veille transmet la requête d'un poste à l'API du serveur 1 (méthode, corps et réponse en flux, aucune mise en cache, rien
/// d'écrit sur le disque). <c>Authorization</c> et <c>X-Pharma-Poste</c> restent INCHANGÉS : le serveur 1 vérifie lui-même jeton, révocations et poste lié.
/// Les en-têtes de saut à saut, <c>Forwarded</c>/<c>X-Forwarded-*</c> et tout <c>X-Pharma-Relais-*</c> reçu du poste sont retirés ; les en-têtes
/// <c>X-Pharma-Relais-*</c> signés (poste, adresse d'origine, horodatage, nonce, HMAC) sont posés par ce serveur seul.
/// Refus (réponse standard 503 « serveur_en_veille », renvoyée par le barrage quand <see cref="TenterAsync"/> rend faux) : poste sans rapport « serveur 1 perdu »
/// de moins de 45 s, verdict de la vigie autre que Relais, serveur 1 non joint, route non relayable. Aucune nouvelle tentative : une requête n'est envoyée qu'une fois.
/// </summary>
public sealed class RelaisVeille(IHttpClientFactory fabrique, SignatureInterServeurs? signature, IConfiguration config, ISituationRelais situation,
    AlerteRelaisPoste alerte, LimiteRelais limite, ILogger<RelaisVeille> journal) : IRelaisVeille
{
    public const string NomClient = "relais";

    private static readonly HashSet<string> SautASaut = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization", "Proxy-Connection", "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    // Retirés de la requête du poste : posés par le relais lui-même, ou annonces d'adresse que seul un proxy déclaré peut faire
    private static readonly HashSet<string> RetiresRequete = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Expect", "Content-Length", "Forwarded", "X-Forwarded-For", "X-Forwarded-Proto", "X-Forwarded-Host", "X-Forwarded-Port", "X-Real-IP",
        "X-Pharma-Serveur", "X-Pharma-Horodatage", "X-Pharma-Nonce", "X-Pharma-Signature"
    };

    public async Task<bool> TenterAsync(HttpContext ctx, CancellationToken ct)
    {
        var @base = ReglagesInterServeurs.AdresseApiAutre(config);
        if (signature is null || @base is null) return false;   // un seul serveur (ou clé absente) : relais inactif
        var req = ctx.Request;
        if (!req.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) || BarrageServeurVeille.Permise(req.Path)) return false;
        if (string.IsNullOrWhiteSpace(req.Method) || HttpMethods.IsConnect(req.Method) || HttpMethods.IsTrace(req.Method)) return false;
        // En-tête X-Pharma-Poste : nom encodé URL par le poste, décodé ici en forme canonique (NomPoste) ; signé sous sa forme ASCII encodée
        if (!req.Headers.TryGetValue(PresencePostesMiddleware.EnTete, out var valeur) || valeur.Count != 1
            || NomPoste.DepuisEnTete(valeur[0]) is not { } poste) return false;
        // Liste des postes autorisés : nom du poste PROUVÉ par sa signature (clé publique de la copie répliquée) ; preuve toujours exigée (script 174,
        // le mode vaut toujours « exige ») : preuve fausse ou absente, jamais relayée (branches « transition » gardées, jamais prises)
        if (ctx.RequestServices.GetService<Pharma.Application.Postes.RegistrePostesAutorises>() is { } registre
            && await registre.ModeAsync(ct) is var mode && mode != Pharma.Shared.PreuvePoste.ModeInactif)
        {
            var entetes = PreuvePosteMiddleware.EnTetes(req.Headers);
            if (entetes.Presents)
            {
                var r = await ctx.RequestServices.GetRequiredService<VerificateurPreuvePoste>().VerifierAsync(entetes, req.Method, InterServeursMiddleware.CibleBrute(ctx),
                    req.Headers.Authorization.FirstOrDefault(), ct);
                if (r.Poste is { } prouve) poste = prouve.Nom;
                // Preuve fausse ou inconnue : relayée seulement en mode « transition », comme un poste sans preuve ; révoqué, horloge : jamais
                else if (mode != Pharma.Shared.PreuvePoste.ModeTransition || r.Issue != IssuePreuve.NonProuve) return false;
            }
            else if (mode == Pharma.Shared.PreuvePoste.ModeExige) return false;
        }
        var posteSigne = NomPoste.VersEnTete(poste);
        if (OrigineRelais.Texte(ctx.Connection.RemoteIpAddress) is not { } origine) return false;
        if (situation.Evaluer(poste) is { } refus)
        {
            journal.LogDebug("Relais refusé pour un poste : {Cause}", refus);
            return false;
        }

        if (!limite.Autoriser(poste, origine))
        {
            journal.LogWarning("Relais : limite de débit atteinte pour un poste");
            ctx.Response.Headers.RetryAfter = "60";
            await Probleme(ctx, StatusCodes.Status429TooManyRequests, "Trop de requêtes", "Trop de requêtes relayées depuis ce poste : patientez une minute.", "relais_trop_de_requetes");
            return true;
        }
        situation.Relayee(poste);
        // Cible brute (telle que reçue) : la signature du poste porte sur elle, le serveur 1 la vérifie à l'identique
        var cible = InterServeursMiddleware.CibleBrute(ctx);
        using var message = new HttpRequestMessage(new HttpMethod(req.Method), new Uri(@base, cible));
        if (CorpsPresent(ctx))
        {
            if (DelaisRelais.TailleCorpsMax(req.Path.Value ?? "") is { } taille && ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } f)
                f.MaxRequestBodySize = taille;
            message.Content = new StreamContent(req.Body);
            if (req.ContentLength is { } longueur) message.Content.Headers.ContentLength = longueur;
        }
        CopierEnTetesRequete(req.Headers, message);
        var signe = signature.SignerRelais(posteSigne, origine, req.Method, message.RequestUri!.PathAndQuery);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Serveur, signe.Serveur);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Poste, signe.Poste);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Origine, signe.Origine);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Horodatage, signe.Horodatage);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Nonce, signe.Nonce);
        message.Headers.TryAddWithoutValidation(EnTetesRelais.Signature, signe.Signature);

        if (situation.AlerteAutorisee(poste) && alerte.Signaler(poste) is not null)
            journal.LogInformation("Relais : le poste {Poste} passe par ce serveur (serveur 1 non joint par ce poste)", poste);

        using var delai = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delai.CancelAfter(DelaisRelais.Pour(req.Path.Value ?? ""));
        HttpResponseMessage reponse;
        try
        {
            reponse = await fabrique.CreateClient(NomClient).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, delai.Token);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) return true;   // le poste a abandonné : rien à répondre
            if (e is OperationCanceledException)
            {
                journal.LogWarning("Relais : délai dépassé en attendant le serveur 1");
                await Probleme(ctx, StatusCodes.Status504GatewayTimeout, "Délai dépassé", "Le serveur principal n'a pas répondu dans le délai prévu.", "relais_delai_depasse");
                return true;
            }
            journal.LogWarning("Relais : serveur 1 non joint ({Type})", e.GetType().Name);
            return false;   // rien n'est parti vers le poste : réponse standard 503 « serveur_en_veille »
        }

        using (reponse)
        {
            ctx.Response.StatusCode = (int)reponse.StatusCode;
            var nommes = NomsConnexion(reponse.Headers.Connection);
            foreach (var h in reponse.Headers)
                if (!SautASaut.Contains(h.Key) && !nommes.Contains(h.Key)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
            foreach (var h in reponse.Content.Headers)
                if (!string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) ctx.Response.Headers[h.Key] = h.Value.ToArray();
            if (reponse.Content.Headers.ContentLength is { } n) ctx.Response.ContentLength = n;
            if (!ctx.Response.Headers.ContainsKey("Cache-Control")) ctx.Response.Headers.CacheControl = "no-store";
            try
            {
                await using var flux = await reponse.Content.ReadAsStreamAsync(delai.Token);
                await flux.CopyToAsync(ctx.Response.Body, delai.Token);
            }
            catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
            {
                // Réponse déjà commencée : impossible de la corriger, la connexion est coupée (le poste voit un échec réseau)
                if (!ct.IsCancellationRequested) journal.LogWarning("Relais : réponse du serveur 1 interrompue ({Type})", e.GetType().Name);
                ctx.Abort();
            }
        }
        return true;
    }

    private static bool CorpsPresent(HttpContext ctx)
    {
        var req = ctx.Request;
        if (req.ContentLength == 0) return false;
        return ctx.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody ?? (req.ContentLength > 0 || req.Headers.ContainsKey("Transfer-Encoding"));
    }

    private static void CopierEnTetesRequete(IHeaderDictionary entetes, HttpRequestMessage message)
    {
        var nommes = NomsConnexion(entetes.Connection);
        foreach (var (nom, valeurs) in entetes)
        {
            if (SautASaut.Contains(nom) || RetiresRequete.Contains(nom) || EnTetesRelais.EstEnTeteRelais(nom) || nommes.Contains(nom)) continue;
            var v = valeurs.ToArray();
            if (!message.Headers.TryAddWithoutValidation(nom, v)) message.Content?.Headers.TryAddWithoutValidation(nom, v);
        }
    }

    private static HashSet<string> NomsConnexion(IEnumerable<string> valeurs) =>
        valeurs.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static Task Probleme(HttpContext ctx, int statut, string titre, string detail, string code) =>
        Results.Problem(title: titre, detail: detail, statusCode: statut, type: $"urn:pharmaofficine:erreur:{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(ctx);
}

public static class RelaisServices
{
    /// <summary>
    /// Enregistre le relais seulement si deux serveurs sont déclarés (SecondServeur:AutreServeur) et la clé des jetons présente ; sinon rien n'est enregistré
    /// (officine à un seul serveur : relais inactif, requête refusée par le barrage). À appeler après AjouterVigieSecondServeur.
    /// </summary>
    public static IServiceCollection AjouterRelaisVeille(this IServiceCollection services, IConfiguration config)
    {
        if (!DeuxServeursDeclares.Est(config["SecondServeur:AutreServeur"]) || string.IsNullOrWhiteSpace(config["Jwt:Cle"])) return services;
        // Pas de délai global du client (délais par route dans RelaisVeille), ni redirection, ni cookie, ni proxy ; aucun journal d'URL (la requête peut citer un nom)
        services.AddHttpClient(RelaisVeille.NomClient)
            .ConfigureHttpClient(c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseProxy = false, AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(2)
            })
            .RemoveAllLoggers();
        services.AddSingleton<ISituationRelais>(sp => new SituationRelais(sp.GetRequiredService<VigieSecondServeur>(), sp.GetRequiredService<RegistreRapportsPostes>(),
            sp.GetRequiredService<SondeServeur1>(), sp.GetRequiredService<Pharma.Application.Commun.IHorloge>(), sp.GetRequiredService<ReglagesBascule>()));
        services.AddSingleton(sp => new AlerteRelaisPoste(sp.GetRequiredService<IAlertesVersServeur1>(), sp.GetRequiredService<Pharma.Application.Commun.IHorloge>(),
            sp.GetService<Pharma.Application.Commun.ITraceErreurs>()));
        services.AddSingleton(sp => new LimiteRelais(sp.GetRequiredService<Pharma.Application.Commun.IHorloge>()));
        services.AddSingleton<IRelaisVeille>(sp => new RelaisVeille(sp.GetRequiredService<IHttpClientFactory>(), sp.GetService<SignatureInterServeurs>(),
            sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<ISituationRelais>(), sp.GetRequiredService<AlerteRelaisPoste>(), sp.GetRequiredService<LimiteRelais>(),
            sp.GetRequiredService<ILogger<RelaisVeille>>()));
        return services;
    }
}
