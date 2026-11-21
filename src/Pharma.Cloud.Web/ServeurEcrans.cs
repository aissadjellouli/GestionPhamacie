using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Npgsql;
using Pharma.Cloud.Api.Depart;
using Pharma.Cloud.Web.Codes;
using Pharma.Cloud.Web.Composants;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Points;
using Pharma.Cloud.Web.Securite;

namespace Pharma.Cloud.Web;

/// <summary>Options de construction (tests : port 0, certificat de test, envoi des codes remplacé).</summary>
public sealed class OptionsServeurEcrans
{
    public X509Certificate2? Certificat { get; init; }
    public IEnvoiCodeDistant? Envoi { get; init; }
    public TimeProvider? Horloge { get; init; }
}

/// <summary>
/// Serveur des écrans du central (lot S7.1) : Blazor en rendu serveur statique, HTTPS seulement, cookie __Host-, jeton
/// anti-falsification, CSP stricte, Cache-Control: no-store, limites de débit par adresse. Aucune route de l'API des officines.
/// </summary>
public static class ServeurEcrans
{
    public const string Csp = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'self'; "
                              // base-uri 'self' (et non 'none') : la balise <base href="/"> d'App.razor doit s'appliquer, sinon les adresses
                              // relatives (css/, js/, img/, liens) sont cherchées sous /officines/… sur les pages imbriquées (404, script de
                              // l'écran fixe absent ; constaté le 2026-11-21) ; une base vers un autre site reste interdite
                              + "form-action 'self'; frame-ancestors 'none'; base-uri 'self'";

    public static async Task<int> LancerAsync(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var (reglages, refus) = ReglagesEcrans.Evaluer(builder.Environment.IsDevelopment(), builder.Configuration, Environment.GetEnvironmentVariable);
        if (reglages is null)
        {
            Console.Error.WriteLine(refus);
            return 1;
        }
        IEnvoiCodeDistant envoi;
        switch (reglages.Mode)
        {
            case ModeCodes.Fichier:
                envoi = new EnvoiCodeFichier(reglages.DossierCodes, TimeProvider.System);
                break;
            default:
                var (smtp, manque) = ReglagesSmtpCentral.Lire(Environment.GetEnvironmentVariable);
                if (smtp is null)
                {
                    Console.Error.WriteLine($"Écrans refusés : {manque} (envoi des codes de second facteur par e-mail).");
                    return 1;
                }
                envoi = new EnvoiCodeCourriel(new EnvoiCourrielSmtp(smtp));
                break;
        }
        var app = Construire(builder, reglages, new OptionsServeurEcrans { Envoi = envoi });
        try
        {
            app.Logger.LogInformation("Connexion des écrans : rôle {Role}", await app.Services.GetRequiredService<DepotEcrans>().ControlerRoleAsync(CancellationToken.None));
        }
        catch (Exception e) when (e is InvalidOperationException or NpgsqlException)
        {
            Console.Error.WriteLine($"Écrans refusés : {e.Message}");
            return 1;
        }
        app.Logger.LogInformation("Écrans du central : https://{Adresse}:{Port}/ (codes : {Mode})", reglages.Adresse, reglages.Port, reglages.Mode);
        await app.RunAsync();
        return 0;
    }

    /// <summary>Construit l'application (non démarrée). Le builder fourni porte l'environnement (Development pour le mode fichier).</summary>
    public static WebApplication Construire(WebApplicationBuilder builder, ReglagesEcrans reglages, OptionsServeurEcrans options)
    {
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 16 * 1024;
            k.Listen(reglages.Adresse, reglages.Port, l => l.UseHttps(h =>
            {
                if (options.Certificat is not null) h.ServerCertificate = options.Certificat;
                h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
            }));
        });
        var horloge = options.Horloge ?? TimeProvider.System;
        builder.Services.AddSingleton(horloge);
        builder.Services.AddSingleton(NpgsqlDataSource.Create(reglages.Connexion));
        builder.Services.AddSingleton<DepotEcrans>();
        builder.Services.AddSingleton(new CodesSecondFacteur());
        builder.Services.AddSingleton(options.Envoi ?? new EnvoiCodeFichier(reglages.DossierCodes, horloge));
        builder.Services.AddSingleton(new EpinglageAutorite(reglages.CertificatAutorite));
        builder.Services.AddSingleton<CodesAffiches>();
        builder.Services.AddRazorComponents();
        builder.Services.AddAntiforgery(o =>
        {
            o.Cookie.Name = Jetons.CookieAntiFalsification;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;
            o.Cookie.Path = "/";
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Derrière un proxy inverse, l'adresse vue est celle du proxy : [À VÉRIFIER] à la mise en ligne
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
                "ip:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            o.AddPolicy("connexion", ctx => RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
            o.AddPolicy("code", ctx => RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress,
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(5), QueueLimit = 0 }));
        });
        if (!builder.Environment.IsDevelopment()) builder.Services.AddHsts(o => { o.MaxAge = TimeSpan.FromDays(365); o.IncludeSubDomains = true; });

        var app = builder.Build();
        if (!app.Environment.IsDevelopment()) app.UseHsts();
        // Requête mal formée (jeton anti-falsification absent ou faux…) : 400, jamais 500 ; page d'erreur sans détail technique
        app.UseExceptionHandler(new ExceptionHandlerOptions
        {
            ExceptionHandler = PageErreurAsync,
            StatusCodeSelector = e => e is BadHttpRequestException b ? b.StatusCode : StatusCodes.Status500InternalServerError,
        });
        app.Use(async (ctx, suite) =>
        {
            var h = ctx.Response.Headers;
            h.CacheControl = "no-store";
            h.Pragma = "no-cache";
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h.ContentSecurityPolicy = Csp;
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            await suite();
        });
        app.UseStatusCodePages(ctx => PageErreurAsync(ctx.HttpContext));
        app.UseStaticFiles();
        app.UseRateLimiter();
        app.Use((ctx, suite) => ControleAcces.Appliquer(ctx, suite));
        app.UseAntiforgery();
        ActionsEcrans.Mapper(app);
        app.MapRazorComponents<App>();
        return app;
    }

    /// <summary>Page d'erreur fixe (aucun détail technique, aucun texte de la requête), même pour un formulaire refusé.</summary>
    private static Task PageErreurAsync(HttpContext ctx)
    {
        var code = ctx.Response.StatusCode;
        var titre = code switch { 404 => "Page introuvable", 429 => "Trop de demandes : attendez quelques minutes", 400 => "Demande refusée", _ => "Erreur" };
        ctx.Response.ContentType = "text/html; charset=utf-8";
        return ctx.Response.WriteAsync($"""
            <!DOCTYPE html><html lang="fr"><head><meta charset="utf-8" /><title>{titre}</title><link rel="stylesheet" href="/css/app.css" /></head>
            <body><div class="page page-serree"><div class="titre-page"><div><h1>{titre}</h1>
            <p class="sous-titre">La demande n'a pas abouti (code {code}). Aucun détail technique n'est affiché ici.</p></div>
            <div class="actions"><a class="btn btn-contour" href="/officines">Officines</a></div></div></div></body></html>
            """);
    }
}
