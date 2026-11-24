using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Pharma.Api.Securite;

/// <summary>
/// Sécurité du réseau de l'API : HTTP en clair refusé sur le réseau hors développement (sauf réglage explicite
/// AutoriserHttpReseau=true), HSTS et redirection HTTPS quand HTTPS est configuré, limite de débit par adresse IP sur les
/// routes de connexion, contrôle du fuseau horaire de Windows. Voir docs/mise-en-production.md.
/// </summary>
public static class ReseauSecurise
{
    /// <summary>Réglage qui autorise l'écoute en HTTP sur une adresse du réseau en production (choix assumé, documenté).</summary>
    public const string ReglageHttpReseau = "AutoriserHttpReseau";
    public const string PolitiqueConnexion = "connexion";

    /// <summary>Routes de connexion limitées en débit par adresse IP (mots de passe, codes comptoir, super admin).</summary>
    public static readonly IReadOnlyList<string> RoutesConnexion =
        ["/api/auth/connexion", "/api/auth/connexion-code", "/api/super-admin/connexion", "/api/auth/mot-de-passe", "/api/postes/enrolement"];

    private static readonly string[] HotesLocaux = ["localhost", "127.0.0.1", "[::1]", "::1"];

    /// <summary>Adresses d'écoute configurées : Kestrel:Endpoints:*:Url si présent (prioritaire), sinon Urls (ou ASPNETCORE_URLS).</summary>
    public static IReadOnlyList<string> AdressesEcoute(IConfiguration config)
    {
        var kestrel = config.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>()
            .Where(u => u.Length > 0).ToList();
        if (kestrel.Count > 0) return kestrel;
        return (config["urls"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>Adresses HTTP (en clair) ouvertes sur le réseau : autre hôte que localhost / 127.0.0.1 / ::1 (0.0.0.0, *, +, IP…).</summary>
    public static IReadOnlyList<string> HttpSurLeReseau(IConfiguration config) =>
        AdressesEcoute(config).Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !EstLocale(u)).ToList();

    public static bool HttpsConfigure(IConfiguration config) =>
        AdressesEcoute(config).Any(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    /// <summary>Vrai si l'API écoute ailleurs que sur localhost (HTTP ou HTTPS), ou si aucune adresse n'est configurée (Kestrel écoute alors localhost seulement).</summary>
    public static bool EcouteAilleursQueLocalhost(IConfiguration config) =>
        AdressesEcoute(config).Any(u => !EstLocale(u));

    private static bool EstLocale(string url)
    {
        var reste = url[(url.IndexOf("://", StringComparison.Ordinal) is var i and >= 0 ? i + 3 : 0)..];
        var fin = reste.StartsWith('[') ? reste.IndexOf(']') + 1 : reste.IndexOfAny([':', '/']);
        var hote = fin <= 0 ? reste : reste[..fin];
        return HotesLocaux.Contains(hote, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Hors développement : refus de démarrer si l'API écoute en HTTP sur une adresse du réseau, sauf AutoriserHttpReseau=true.
    /// Null : démarrage permis ; sinon le message d'arrêt.
    /// </summary>
    public static string? RefusDemarrage(IConfiguration config, bool developpement)
    {
        if (developpement || config.GetValue(ReglageHttpReseau, false)) return null;
        var http = HttpSurLeReseau(config);
        return http.Count == 0 ? null
            : $"Démarrage refusé : l'API écoute en HTTP (non chiffré) sur le réseau ({string.Join(", ", http)}). Configurez HTTPS "
              + $"(Kestrel:Endpoints), limitez HTTP à localhost, ou réglez « {ReglageHttpReseau} »: true pour l'accepter en connaissance "
              + "de cause (docs/mise-en-production.md).";
    }

    /// <summary>Limite de débit par adresse IP sur les routes de connexion (réglage LimiteConnexions:ParMinute, 20 par défaut).</summary>
    public static IServiceCollection AjouterLimiteConnexions(this IServiceCollection services, IConfiguration config)
    {
        var parMinute = Math.Max(1, config.GetValue("LimiteConnexions:ParMinute", 20));
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.OnRejected = async (ctx, ct) =>
                await ctx.HttpContext.Response.WriteAsJsonAsync(
                    new Pharma.Shared.ErreurDto("Trop de tentatives de connexion ou de codes depuis ce poste : patientez une minute."), ct);
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                // Connexion, ou requête portant une autorisation par code (essai d'un code comptoir, revue 2026-11-15 I2)
                EstRouteConnexion(ctx.Request.Path) || PorteAutorisationParCode(ctx.Request)
                    ? RateLimitPartition.GetFixedWindowLimiter(ctx.Connection.RemoteIpAddress?.ToString() ?? "inconnue",
                        _ => new FixedWindowRateLimiterOptions { PermitLimit = parMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })
                    : RateLimitPartition.GetNoLimiter(""));
        });
        return services;
    }

    /// <summary>Requête qui fait valider une action par le code comptoir d'un collègue (en-tête X-Pharma-Autorise-Par).</summary>
    public static bool PorteAutorisationParCode(HttpRequest requete) => requete.Headers.ContainsKey(AutorisationParCode.EnTeteIdentifiant);

    public static bool EstRouteConnexion(PathString chemin) =>
        RoutesConnexion.Any(r => string.Equals(chemin.Value?.TrimEnd('/'), r, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// HTTPS configuré (hors développement) : HSTS sur les réponses HTTPS, et redirection vers HTTPS des requêtes HTTP venues
    /// du réseau, sauf si HTTP est volontairement gardé sur le réseau (AutoriserHttpReseau=true). Le poste local (127.0.0.1) n'est
    /// jamais redirigé.
    /// </summary>
    public static void UtiliserHttps(this WebApplication app)
    {
        if (app.Environment.IsDevelopment() || !HttpsConfigure(app.Configuration)) return;
        app.UseHsts();
        if (app.Configuration.GetValue(ReglageHttpReseau, false)) return;
        app.UseWhen(ctx => ctx.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip), b => b.UseHttpsRedirection());
    }

    /// <summary>
    /// Ancien emplacement du certificat public de l'autorité, publié par l'API : il n'est plus distribué par le réseau (clé USB,
    /// voir docs/mise-en-production.md). Si une ancienne copie traîne dans wwwroot, elle n'est servie qu'au PC serveur (127.0.0.1, ::1).
    /// </summary>
    public const string CheminCertificatAutorite = "/mobile/autorite-pharmacie.crt";

    public static bool EstCertificatAutorite(PathString chemin) =>
        string.Equals(chemin.Value, CheminCertificatAutorite, StringComparison.OrdinalIgnoreCase);

    /// <summary>Le certificat de l'autorité n'est servi qu'à une requête venue du poste local, jamais à une requête relayée par le 2e serveur (aucun privilège local par relais).</summary>
    public static bool CertificatAutoriteRefuse(PathString chemin, IPAddress? origine, bool viaRelais = false) =>
        EstCertificatAutorite(chemin) && (viaRelais || origine is null || !IPAddress.IsLoopback(origine));

    /// <summary>
    /// L'officine est en Algérie (UTC+1 toute l'année, sans heure d'été) : avertissement si le fuseau de Windows en diffère
    /// (dates des ventes calculées en C# décalées par rapport à la base). Null : fuseau correct.
    /// </summary>
    public static string? AvertissementFuseau(TimeZoneInfo fuseau, DateTime maintenantUtc)
    {
        var decalage = fuseau.GetUtcOffset(maintenantUtc);
        if (decalage == TimeSpan.FromHours(1) && !fuseau.SupportsDaylightSavingTime) return null;
        return $"Fuseau horaire de Windows « {fuseau.Id} » (UTC{(decalage < TimeSpan.Zero ? "-" : "+")}{decalage:hh\\:mm}"
               + $"{(fuseau.SupportsDaylightSavingTime ? ", avec heure d'été" : "")}) : l'officine est en UTC+1 toute l'année "
               + "(Africa/Algiers, « (UTC+01:00) Afrique centrale - Ouest » dans Windows). Les dates des ventes peuvent être décalées.";
    }
}
