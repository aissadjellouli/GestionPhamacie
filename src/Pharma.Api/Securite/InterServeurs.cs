using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Securite;

// ---- Bascule du 2e serveur, vague 2, lot V2-2 : communication signée entre les deux serveurs (contrat-api.md § 2.3 et § 4) ----------

/// <summary>Réglages lus de la configuration : fenêtre d'horloge (SecondServeur:Bascule:FenetreSignatureSecondes, 30 à 300) et adresse de l'API de l'autre serveur.</summary>
public static class ReglagesInterServeurs
{
    public const int PortApiParDefaut = 5443;

    public static int Fenetre(IConfiguration c) =>
        Math.Clamp(c.GetValue("SecondServeur:Bascule:FenetreSignatureSecondes", ReglagesBasculeParDefaut.FenetreSignatureSecondes),
            SignatureInterServeurs.FenetreMin, SignatureInterServeurs.FenetreMax);

    /// <summary>SecondServeur:AutreServeurApi si réglé (URL https), sinon https://&lt;AutreServeur&gt;:5443. Null : aucun 2e serveur déclaré ou adresse invalide.</summary>
    public static Uri? AdresseApiAutre(IConfiguration c)
    {
        var explicite = c["SecondServeur:AutreServeurApi"];
        if (!string.IsNullOrWhiteSpace(explicite))
            return Uri.TryCreate(explicite.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps ? u : null;
        return ControleAppelantInterServeurs.Hote(c["SecondServeur:AutreServeur"]) is { } hote
            && Uri.TryCreate($"https://{(IPAddress.TryParse(hote, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{hote}]" : hote)}:{PortApiParDefaut}/", UriKind.Absolute, out var d) ? d : null;
    }
}

/// <summary>
/// Intergiciel des routes /api/inter-serveurs/** : pas de jeton d'utilisateur, uniquement la signature. Ordre : serveur déclaré (sinon 404, mécanisme
/// inactif), adresse de l'appelant (403), signature (401), puis la route ; la réponse (tout code) est signée à son tour. Détail du refus : jamais
/// renvoyé à l'appelant (même message pour tous les 401), seulement au journal sans valeur reçue ni secret.
/// </summary>
public sealed class InterServeursMiddleware(RequestDelegate suite, ILogger<InterServeursMiddleware> journal)
{
    public const string Prefixe = "/api/inter-serveurs";
    public const int CorpsMaxOctets = 64 * 1024;
    private static long _dernierJournal;

    public static bool Concerne(PathString chemin) => chemin.StartsWithSegments(Prefixe, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext ctx, SuiviCleJetons suivi)
    {
        if (!Concerne(ctx.Request.Path)) { await suite(ctx); return; }
        var ct = ctx.RequestAborted;
        var signature = ctx.RequestServices.GetService<SignatureInterServeurs>();
        var controle = ctx.RequestServices.GetService<ControleAppelantInterServeurs>();
        // Officine à un seul serveur (ou clé absente) : mécanisme inactif, la route n'existe pas
        if (signature is null || controle is not { Declare: true })
        {
            await Probleme(ctx, StatusCodes.Status404NotFound, "Non disponible", "Aucun deuxième serveur n'est déclaré sur ce serveur.", "inter_serveurs_inactif");
            return;
        }
        if (!await controle.AutoriseAsync(ctx.Connection.RemoteIpAddress, ct))
        {
            Journaliser("appelant non reconnu (adresse privée et adresse déclarée de l'autre serveur exigées)");
            await Probleme(ctx, StatusCodes.Status403Forbidden, "Appelant non reconnu", "Appel refusé.", CodesBasculeVague2.SignatureInvalide);
            return;
        }
        if (ctx.Request.ContentLength > CorpsMaxOctets)
        {
            await Probleme(ctx, StatusCodes.Status413PayloadTooLarge, "Corps trop grand", "Corps refusé.", CodesBasculeVague2.SignatureInvalide);
            return;
        }
        var corps = await LireCorpsAsync(ctx.Request.Body, ct);
        if (corps is null)
        {
            await Probleme(ctx, StatusCodes.Status413PayloadTooLarge, "Corps trop grand", "Corps refusé.", CodesBasculeVague2.SignatureInvalide);
            return;
        }
        var h = ctx.Request.Headers;
        var recus = new EnTetesRecus(h[EnTetesInterServeurs.Serveur].FirstOrDefault(), h[EnTetesInterServeurs.Horodatage].FirstOrDefault(),
            h[EnTetesInterServeurs.Nonce].FirstOrDefault(), h[EnTetesInterServeurs.Signature].FirstOrDefault());
        if (signature.VerifierRequete(ctx.Request.Method, CibleBrute(ctx), recus, corps) is { } refus)
        {
            if (refus == RefusSignature.SignatureFausse) suivi.NoterRefusEntrant();
            Journaliser(refus switch
            {
                RefusSignature.EnTetesInvalides => "en-têtes de signature absents ou invalides",
                RefusSignature.HorodatageHorsFenetre => "horodatage hors fenêtre (horloges des deux serveurs ?)",
                RefusSignature.NonceRejoue => "nonce déjà utilisé (rejeu)",
                _ => "signature fausse (clé des jetons différente ou message modifié)"
            });
            await Probleme(ctx, StatusCodes.Status401Unauthorized, "Signature invalide", "Appel refusé.", CodesBasculeVague2.SignatureInvalide);
            return;
        }

        // Requête authentique : la route s'exécute, sa réponse est mise en mémoire, signée, puis envoyée
        ctx.Request.Body = new MemoryStream(corps, writable: false);
        var origine = ctx.Response.Body;
        await using var tampon = new MemoryStream();
        ctx.Response.Body = tampon;
        try { await suite(ctx); }
        catch
        {
            ctx.Response.Body = origine;   // l'exception remonte au gestionnaire d'erreurs, réponse non signée = rejetée par l'appelant
            throw;
        }
        var octets = tampon.ToArray();
        ctx.Response.Body = origine;
        var signe = signature.SignerReponse(ctx.Response.StatusCode, recus.Nonce!, octets);
        ctx.Response.Headers[EnTetesInterServeurs.Serveur] = signe.Serveur;
        ctx.Response.Headers[EnTetesInterServeurs.Horodatage] = signe.Horodatage;
        ctx.Response.Headers[EnTetesInterServeurs.Nonce] = signe.Nonce;
        ctx.Response.Headers[EnTetesInterServeurs.Signature] = signe.Signature;
        ctx.Response.ContentLength = octets.Length;
        if (octets.Length > 0) await origine.WriteAsync(octets, ct);
    }

    /// <summary>Chemin et requête « tels qu'envoyés » (ligne de requête brute) ; à défaut, chemin et requête reconstitués.</summary>
    internal static string CibleBrute(HttpContext ctx) =>
        ctx.Features.Get<IHttpRequestFeature>()?.RawTarget is { Length: > 0 } brut ? brut : ctx.Request.Path.ToUriComponent() + ctx.Request.QueryString.ToUriComponent();

    private static async Task<byte[]?> LireCorpsAsync(Stream flux, CancellationToken ct)
    {
        using var m = new MemoryStream();
        var tampon = new byte[8192];
        int n;
        while ((n = await flux.ReadAsync(tampon, ct)) > 0)
        {
            if (m.Length + n > CorpsMaxOctets) return null;
            m.Write(tampon, 0, n);
        }
        return m.ToArray();
    }

    private static Task Probleme(HttpContext ctx, int statut, string titre, string detail, string code) =>
        Results.Problem(title: titre, detail: detail, statusCode: statut, type: $"urn:pharmaofficine:erreur:{code}",
            extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(ctx);

    /// <summary>Un refus par 10 s au plus au journal (un inconnu du réseau ne peut pas le noyer) ; ni valeur reçue, ni nom, ni secret.</summary>
    private void Journaliser(string cause)
    {
        var t = Environment.TickCount64;
        var vu = Interlocked.Read(ref _dernierJournal);
        if (t - vu < 10_000 || Interlocked.CompareExchange(ref _dernierJournal, t, vu) != vu) return;
        journal.LogWarning("Appel inter-serveurs refusé : {Cause}", cause);
    }
}

public static class InterServeursEndpoints
{
    private static IResult Probleme(int statut, string titre, string detail, string code, IDictionary<string, object?>? extra = null)
    {
        var ext = new Dictionary<string, object?> { ["code"] = code };
        if (extra is not null) foreach (var (k, v) in extra) ext[k] = v;
        return Results.Problem(title: titre, detail: detail, statusCode: statut, type: $"urn:pharmaofficine:erreur:{code}", extensions: ext);
    }

    /// <summary>Routes signées : etat (les deux serveurs), retrait et alerte (serveur 1). Restent permises serveur écarté (BarrageServeurEcarte).</summary>
    public static void MapInterServeurs(this WebApplication app)
    {
        // POST /api/inter-serveurs/retrait : le 2e serveur met le serveur 1 hors jeu avant de se promouvoir (cas 2). Corps vide.
        app.MapPost(InterServeursMiddleware.Prefixe + "/retrait", async (RetraitServeur retrait, CancellationToken ct) =>
        {
            var r = await retrait.ExecuterAsync(ct);
            return r.Issue switch
            {
                IssueRetrait.Ecarte => Results.Ok(new RetraitInterServeursDto(true, r.LsnFinal!)),
                IssueRetrait.DejaEcarte => Probleme(StatusCodes.Status409Conflict, "Déjà écarté", "Ce serveur est déjà écarté : aucun nouveau retrait.", CodesBasculeVague2.DejaEcarte,
                    r.LsnFinal is null ? null : new Dictionary<string, object?> { ["lsnFinal"] = r.LsnFinal }),
                _ => Probleme(StatusCodes.Status503ServiceUnavailable, "Retrait impossible",
                    "Ce serveur n'a pas pu être mis hors jeu (serveur en veille, 2e serveur non déclaré, ou transactions non terminées dans le délai).", CodesBasculeVague2.RetraitImpossible)
            };
        });

        // POST /api/inter-serveurs/alerte : le 2e serveur (base en lecture seule) fait prévenir le titulaire par le serveur 1 ; 202 sans corps
        app.MapPost(InterServeursMiddleware.Prefixe + "/alerte", async (AlerteInterServeursDto? d, RecevoirAlerteInterServeurs alerte, IVeilleServeur veille, CancellationToken ct) =>
        {
            if (veille.EstVeille)
                return Probleme(StatusCodes.Status503ServiceUnavailable, "Serveur en veille", "Ce serveur est en veille : il ne peut pas prévenir le titulaire.", CodesBasculeVague2.ServeurEnVeille);
            if (d is null) return Probleme(StatusCodes.Status400BadRequest, "Alerte invalide", "Corps absent.", "alerte_invalide");
            return await alerte.ExecuterAsync(d.Type, d.Poste, d.Message, ct) switch
            {
                IssueAlerteRecue.TypeInconnu => Probleme(StatusCodes.Status400BadRequest, "Type d'alerte inconnu", "Type d'alerte inconnu.", "alerte_type_inconnu"),
                IssueAlerteRecue.PosteManquant => Probleme(StatusCodes.Status400BadRequest, "Alerte invalide", "Le poste est obligatoire pour ce type d'alerte.", "alerte_invalide"),
                IssueAlerteRecue.MessageInvalide => Probleme(StatusCodes.Status400BadRequest, "Alerte invalide", "Message ou poste trop long.", "alerte_invalide"),
                _ => Results.StatusCode(StatusCodes.Status202Accepted)   // acceptée, ou déjà partie dans l'heure (limite horaire : le 2e serveur n'a rien de plus à savoir)
            };
        });

        app.MapGet(InterServeursMiddleware.Prefixe + "/etat", async (CeServeur ce, SignatureInterServeurs signature, IServeurEcarteDetecte ecart,
            IVeilleServeur veille, IReleveServeurs releves, ReglagesBascule reglages, CancellationToken ct) =>
        {
            var local = await releves.LocalAsync(ct);
            // Base illisible : rien n'est affirmé (un faux « principal » ou « en recovery » fausserait une décision de bascule)
            if (local is null)
                return Results.Problem(title: "État illisible", detail: "L'état de la base de ce serveur est momentanément illisible.",
                    statusCode: StatusCodes.Status503ServiceUnavailable, type: "urn:pharmaofficine:erreur:etat_illisible",
                    extensions: new Dictionary<string, object?> { ["code"] = "etat_illisible" });
            // Même fonction que le healthcheck (écarté, sinon veille, sinon principal) : les deux routes ne peuvent pas se contredire
            var role = BarrageServeurVeille.Role(ecart.Ecart is not null, veille.EstVeille);
            int? timeline = local.Timeline is { } t and >= 0 and <= int.MaxValue ? (int)t : null;
            return Results.Ok(new EtatInterServeursDto(ce.Nom, role, timeline, local.EnRecovery, ce.VersionApi, signature.EmpreinteJeton, reglages.Empreinte()));
        });
    }
}

// ---- Client signé (pour les lots V2-3, V2-4, V2-5) ---------------------------------------------------------------------------

public enum IssueInterServeurs
{
    /// <summary>Réponse reçue, signée, authentique et liée à la requête : Statut et Corps sont fiables.</summary>
    Ok,
    /// <summary>Aucun 2e serveur déclaré, adresse invalide ou clé absente : rien n'a été envoyé.</summary>
    NonDeclare,
    /// <summary>Réseau, délai dépassé, certificat refusé.</summary>
    Injoignable,
    /// <summary>L'autre serveur a répondu 401/403 : il ne nous reconnaît pas (clé des jetons différente, horloge, adresse).</summary>
    Refuse,
    /// <summary>Réponse non signée, falsifiée, rejouée ou trop ancienne : à ne pas croire (Statut et Corps = informations, jamais une décision).</summary>
    ReponseInvalide,
    /// <summary>Réponse signée mais horodatage hors fenêtre : horloges décalées (ou rejeu).</summary>
    HorlogeDecalee
}

public sealed record ReponseInterServeurs(IssueInterServeurs Issue, int? Statut, byte[] Corps)
{
    public bool Fiable => Issue == IssueInterServeurs.Ok;
}

/// <summary>Résultat de la lecture de l'état de l'autre serveur ; Etat n'est renseigné que si Issue = Ok.</summary>
public sealed record EtatAutreServeur(IssueInterServeurs Issue, int? Statut, EtatInterServeursDto? Etat, bool? MemeCleJetons);

public interface IClientInterServeurs
{
    /// <summary>Vrai si un 2e serveur est déclaré et la clé disponible.</summary>
    bool Disponible { get; }
    /// <summary>Envoie une requête signée (chemin commençant par /api/, requête comprise) ; ne lève rien sauf annulation par <paramref name="ct"/>.</summary>
    Task<ReponseInterServeurs> EnvoyerAsync(HttpMethod methode, string cheminEtRequete, byte[]? corps, TimeSpan delai, CancellationToken ct);
    Task<EtatAutreServeur> LireEtatAutreAsync(CancellationToken ct);
}

public sealed class ClientInterServeurs(IHttpClientFactory fabrique, SignatureInterServeurs? signature, IConfiguration config, ILogger<ClientInterServeurs> journal)
    : IClientInterServeurs
{
    public const string NomClient = "inter-serveurs";
    public static readonly TimeSpan DelaiSonde = TimeSpan.FromSeconds(3);
    private const int ReponseMaxOctets = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public bool Disponible => signature is not null && ReglagesInterServeurs.AdresseApiAutre(config) is not null;

    public async Task<ReponseInterServeurs> EnvoyerAsync(HttpMethod methode, string cheminEtRequete, byte[]? corps, TimeSpan delai, CancellationToken ct)
    {
        var @base = ReglagesInterServeurs.AdresseApiAutre(config);
        if (signature is null || @base is null || !cheminEtRequete.StartsWith("/api/", StringComparison.Ordinal))
            return new ReponseInterServeurs(IssueInterServeurs.NonDeclare, null, []);
        var octets = corps ?? [];
        using var requete = new HttpRequestMessage(methode, new Uri(@base, cheminEtRequete));
        // Ce qui est signé est ce qui part sur le fil : chemin et requête de l'URI absolue construite
        var signe = signature.SignerRequete(methode.Method, requete.RequestUri!.PathAndQuery, octets);
        requete.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Serveur, signe.Serveur);
        requete.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Horodatage, signe.Horodatage);
        requete.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Nonce, signe.Nonce);
        requete.Headers.TryAddWithoutValidation(EnTetesInterServeurs.Signature, signe.Signature);
        if (octets.Length > 0)
        {
            requete.Content = new ByteArrayContent(octets);
            requete.Content.Headers.ContentType = new("application/json");
        }
        using var delaiCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        delaiCts.CancelAfter(delai);
        try
        {
            using var reponse = await fabrique.CreateClient(NomClient).SendAsync(requete, HttpCompletionOption.ResponseHeadersRead, delaiCts.Token);
            var recu = await LireAsync(reponse, delaiCts.Token);
            var statut = (int)reponse.StatusCode;
            var h = reponse.Headers;
            var entetes = new EnTetesRecus(Premier(h, EnTetesInterServeurs.Serveur), Premier(h, EnTetesInterServeurs.Horodatage),
                Premier(h, EnTetesInterServeurs.Nonce), Premier(h, EnTetesInterServeurs.Signature));
            var verdict = recu is null ? RefusSignature.EnTetesInvalides : signature.VerifierReponse(statut, signe.Nonce, entetes, recu);
            if (verdict is null) return new ReponseInterServeurs(IssueInterServeurs.Ok, statut, recu!);
            // Refus de l'autre serveur : ses 401/403 ne sont pas signés (il ne sait pas à qui il parle)
            if (statut is 401 or 403 && string.IsNullOrEmpty(entetes.Signature))
                return new ReponseInterServeurs(IssueInterServeurs.Refuse, statut, recu ?? []);
            journal.LogWarning("Réponse de l'autre serveur rejetée ({Cause})", verdict == RefusSignature.HorodatageHorsFenetre ? "horodatage hors fenêtre" : "signature absente ou fausse");
            return new ReponseInterServeurs(verdict == RefusSignature.HorodatageHorsFenetre ? IssueInterServeurs.HorlogeDecalee : IssueInterServeurs.ReponseInvalide,
                statut, recu ?? []);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            journal.LogDebug("Autre serveur injoignable ({Type})", e.GetType().Name);
            return new ReponseInterServeurs(IssueInterServeurs.Injoignable, null, []);
        }
    }

    public async Task<EtatAutreServeur> LireEtatAutreAsync(CancellationToken ct)
    {
        var r = await EnvoyerAsync(HttpMethod.Get, InterServeursMiddleware.Prefixe + "/etat", null, DelaiSonde, ct);
        if (!r.Fiable || r.Statut != 200) return new EtatAutreServeur(r.Fiable ? IssueInterServeurs.ReponseInvalide : r.Issue, r.Statut, null, null);
        EtatInterServeursDto? etat;
        try { etat = JsonSerializer.Deserialize<EtatInterServeursDto>(r.Corps, Json); }
        catch (JsonException) { etat = null; }
        if (etat is null) return new EtatAutreServeur(IssueInterServeurs.ReponseInvalide, r.Statut, null, null);
        // Les empreintes sont comparées en temps constant ; une empreinte absente = inconnu (pas « différent »)
        bool? meme = etat.EmpreinteJeton is null ? null : CleInterServeurs.EmpreintesEgales(etat.EmpreinteJeton, signature!.EmpreinteJeton);
        return new EtatAutreServeur(IssueInterServeurs.Ok, r.Statut, etat, meme);
    }

    private static string? Premier(System.Net.Http.Headers.HttpResponseHeaders h, string nom) => h.TryGetValues(nom, out var v) ? v.FirstOrDefault() : null;

    private static async Task<byte[]?> LireAsync(HttpResponseMessage reponse, CancellationToken ct)
    {
        if (reponse.Content.Headers.ContentLength > ReponseMaxOctets) return null;
        await using var flux = await reponse.Content.ReadAsStreamAsync(ct);
        using var m = new MemoryStream();
        var tampon = new byte[8192];
        int n;
        while ((n = await flux.ReadAsync(tampon, ct)) > 0)
        {
            if (m.Length + n > ReponseMaxOctets) return null;
            m.Write(tampon, 0, n);
        }
        return m.ToArray();
    }
}

/// <summary>
/// Chaque minute : l'autre serveur reconnaît-il notre signature, et inversement (clés des jetons identiques) ? Verdict dans <see cref="SuiviCleJetons"/> ;
/// si « différent », alerte au titulaire (au plus 1 par heure) par le serveur qui écrit (principal, non écarté) : sur le 2e serveur (base en lecture seule),
/// seul le journal est possible, c'est le serveur 1 qui alerte (il voit les refus de signature venant de l'adresse déclarée de l'autre serveur).
/// Aucun 2e serveur déclaré : rien ne tourne.
/// </summary>
public sealed class SurveillanceInterServeurs(IServiceProvider services, IConfiguration config, SuiviCleJetons suivi, ILogger<SurveillanceInterServeurs> journal) : BackgroundService
{
    public static readonly TimeSpan Intervalle = TimeSpan.FromMinutes(1);
    public const string Objet = "Clés des jetons différentes entre les deux serveurs";
    public const string Detail = "Les deux serveurs ne se reconnaissent pas : la clé des jetons (Jwt__Cle) n'est pas la même sur les deux (ou leurs horloges diffèrent de plus d'une minute). "
        + "Conséquences : la bascule se fera sans reconnexion automatique des postes, le retrait et le relais du 2e serveur sont impossibles. "
        + "Remettez la même clé sur le 2e serveur (docs/second-serveur-local.md), puis redémarrez son API.";
    public const string Sms = "Les 2 serveurs n'ont pas la meme cle des jetons : bascule sans reconnexion, retrait et relais impossibles.";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!DeuxServeursDeclares.Est(config["SecondServeur:AutreServeur"]) || services.GetService<SignatureInterServeurs>() is null) return;
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); }
        catch (OperationCanceledException) { return; }
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await ControlerAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                journal.LogError(ex, "Contrôle de la clé des jetons impossible");
            }
            try { await Task.Delay(Intervalle, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Un passage : sonde signée, verdict, alerte éventuelle. Public pour les tests.</summary>
    public async Task ControlerAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var etat = await scope.ServiceProvider.GetRequiredService<IClientInterServeurs>().LireEtatAutreAsync(ct);
        var verdict = Verdict(etat, suivi.RefusEntrantsSuspects);
        var avant = suivi.Verdict;
        suivi.Mettre(verdict);
        if (verdict != avant)
            journal.LogWarning("Clé des jetons entre les deux serveurs : {Verdict} (sonde : {Issue})", verdict, etat.Issue);
        await ControlerReglagesAsync(etat, scope, ct);
        if (verdict != VerdictCleJetons.Different) return;

        // Seul le serveur qui écrit alerte (l'alerte est une écriture en base) ; le 2e serveur et un serveur écarté : journal seulement
        if (services.GetRequiredService<IServeurEcarteDetecte>().Ecart is not null) return;
        var local = await scope.ServiceProvider.GetRequiredService<IReleveServeurs>().LocalAsync(ct);
        if (local is not { EnRecovery: false } || !suivi.AlerteDue()) return;
        journal.LogCritical("Clés des jetons différentes entre les deux serveurs : titulaire prévenu");
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(
            () => scope.ServiceProvider.GetRequiredService<IAlertesTitulaire>().SecondServeurAsync(Objet, Detail, Sms, ct), "clés des jetons différentes", scope.ServiceProvider.GetService<Pharma.Application.Commun.ITraceErreurs>());
    }

    /// <summary>Réglages de la règle de majorité (délais) différents entre les deux serveurs : journal, et alerte au titulaire (au plus une par heure) par le serveur qui écrit.</summary>
    private async Task ControlerReglagesAsync(EtatAutreServeur etat, IServiceScope scope, CancellationToken ct)
    {
        if (etat.Etat?.Reglages is not { Length: > 0 } autres || services.GetService<ReglagesBascule>() is not { } locaux) return;   // autre serveur plus ancien ou illisible : rien à comparer
        var difference = !string.Equals(autres, locaux.Empreinte(), StringComparison.Ordinal);
        var avant = suivi.ReglagesDifferents;
        suivi.MettreReglages(difference);
        if (!difference) return;
        if (!avant) journal.LogWarning("Réglages SecondServeur:Bascule différents entre les deux serveurs (délais de la règle de majorité)");
        if (services.GetRequiredService<IServeurEcarteDetecte>().Ecart is not null) return;
        var local = await scope.ServiceProvider.GetRequiredService<IReleveServeurs>().LocalAsync(ct);
        if (local is not { EnRecovery: false } || !suivi.AlerteReglagesDue()) return;
        await AlerteNonEnvoyeeSignalee.SansBlocageAsync(
            () => scope.ServiceProvider.GetRequiredService<IAlertesTitulaire>().SecondServeurAsync(ObjetReglages, DetailReglages, SmsReglages, ct), "réglages de bascule différents", scope.ServiceProvider.GetService<Pharma.Application.Commun.ITraceErreurs>());
    }

    public const string ObjetReglages = "Réglages de bascule différents entre les deux serveurs";
    public const string DetailReglages = "Les délais de la règle de majorité (SecondServeur:Bascule : PosteVuSecondes, IsolementSecondes, SondeSecondes, PromotionMinimumSecondes…) ne sont pas les mêmes sur les deux serveurs. "
        + "Avec des délais différents, le 2e serveur pourrait se promouvoir avant que le serveur principal se soit écarté. Mettez les mêmes réglages sur les deux serveurs puis redémarrez leurs API.";
    public const string SmsReglages = "Les 2 serveurs n'ont pas les memes delais de bascule : mettez les memes reglages.";

    /// <summary>
    /// Différent : l'autre serveur nous refuse (401/403), sa réponse ne se vérifie pas avec notre clé, ou son empreinte est autre ; ou plusieurs de ses appels
    /// ont été refusés pour signature fausse alors que notre propre sonde ne prouve pas l'égalité. Identique : réponse signée vérifiée ET empreintes égales.
    /// Horloge décalée, injoignable, non déclaré : inconnu (on n'affirme rien).
    /// </summary>
    public static VerdictCleJetons Verdict(EtatAutreServeur etat, bool refusEntrantsSuspects)
    {
        var sonde = etat.Issue switch
        {
            IssueInterServeurs.Ok when etat.MemeCleJetons == true => VerdictCleJetons.Identique,
            IssueInterServeurs.Ok when etat.MemeCleJetons == false => VerdictCleJetons.Different,
            IssueInterServeurs.Refuse or IssueInterServeurs.ReponseInvalide => VerdictCleJetons.Different,
            _ => VerdictCleJetons.Inconnu
        };
        return sonde == VerdictCleJetons.Identique ? sonde : refusEntrantsSuspects ? VerdictCleJetons.Different : sonde;
    }
}
