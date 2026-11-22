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
    /// <summary>Envoi imposé (tests) : prioritaire sur le réglage en base.</summary>
    public IEnvoiCodeDistant? Envoi { get; init; }
    /// <summary>Production : envoi si le réglage en base est incomplet (variables PHARMA_CLOUD_SMTP_*) ; null : refus clair.</summary>
    public IEnvoiCodeDistant? Repli { get; init; }
    /// <summary>Envoi SMTP pour des réglages (tests : envoi factice, rien ne part).</summary>
    public Func<ReglagesSmtpCentral, IEnvoiCourriel>? FabriqueCourriel { get; init; }
    public TimeProvider? Horloge { get; init; }
    /// <summary>Script central 040 : envoi du mot de passe provisoire par SMS (aucun fournisseur aujourd'hui ; tests : envoi factice).</summary>
    public Pharma.Shared.IEnvoiSmsMotDePasse? SmsMotDePasse { get; init; }
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
        // Journal en fichiers (section « Journaux » du appsettings.json : dossier, taille maximale, durée de conservation, niveau)
        builder.Logging.AddProvider(new Pharma.Shared.Journalisation.JournalFichierProvider(
            Pharma.Shared.Journalisation.OptionsJournalFichier.Lire(cle => builder.Configuration["Journaux:" + cle], "central-ecrans-")));
        var (reglages, refus) = ReglagesEcrans.Evaluer(builder.Environment.IsDevelopment(), builder.Configuration, Environment.GetEnvironmentVariable);
        if (reglages is null)
        {
            Console.Error.WriteLine(refus);
            return 1;
        }
        // Lot S7.6 : le canal et le SMTP viennent du réglage en base (écran « Envoi des codes »), lus à chaque envoi. Repli si le réglage
        // est incomplet : fichier en développement ; en production, variables PHARMA_CLOUD_SMTP_* si complètes (amorçage, comme avant)
        IEnvoiCodeDistant? repli = null;
        if (!builder.Environment.IsDevelopment() && ReglagesSmtpCentral.Lire(Environment.GetEnvironmentVariable).Reglages is { } smtpVariables)
            repli = new EnvoiCodeCourriel(new EnvoiCourrielSmtp(smtpVariables));
        var app = Construire(builder, reglages, new OptionsServeurEcrans { Repli = repli });
        try
        {
            app.Logger.LogInformation("Connexion des écrans : rôle {Role}", await app.Services.GetRequiredService<DepotEcrans>().ControlerRoleAsync(CancellationToken.None));
        }
        catch (Exception e) when (e is InvalidOperationException or NpgsqlException)
        {
            Console.Error.WriteLine($"Écrans refusés : {e.Message}");
            return 1;
        }
        app.Logger.LogInformation("Écrans du central : https://{Adresse}:{Port}/ (codes : {Mode}, clés du mot de passe SMTP : {Cles})", reglages.Adresse, reglages.Port,
            reglages.Mode, app.Services.GetRequiredService<ChoixEnvoiCodes>().Protection is null ? "absentes" : "dossier du central");
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
        // Lot S7.6 : envoi choisi à chaque code d'après le réglage en base ; le mode fichier (développement) ou l'envoi des tests reste prioritaire
        var dev = builder.Environment.IsDevelopment();
        // Mode fichier en DÉVELOPPEMENT : code écrit dans le fichier ET envoyé par l'e-mail du réglage en base s'il est complet ;
        // hors développement (ou envoi fixé par les tests), le mode fichier reste prioritaire et la base n'est pas lue
        var fichierEtMail = options.Envoi is null && reglages.Mode == ModeCodes.Fichier && dev;
        builder.Services.AddSingleton(sp => new ChoixEnvoiCodes(
            options.Envoi ?? (reglages.Mode == ModeCodes.Fichier && !fichierEtMail ? new EnvoiCodeFichier(reglages.DossierCodes, horloge) : null),
            dev ? new EnvoiCodeFichier(reglages.DossierCodes, horloge) : options.Repli,
            (jeton, ct) => sp.GetRequiredService<DepotEcrans>().ReglageEnvoiAsync(jeton, ct),
            ProtectionSmtpCentral.Depuis(reglages.DossierCles),
            options.FabriqueCourriel ?? (smtp => new EnvoiCourrielSmtp(smtp)),
            horloge, sp.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Cloud.Web.Codes"),
            copieFichier: fichierEtMail ? new EnvoiCodeFichier(reglages.DossierCodes, horloge) : null));
        builder.Services.AddSingleton(new EpinglageAutorite(reglages.CertificatAutorite));
        builder.Services.AddSingleton<CodesAffiches>();
        // Script central 040 : import de comptes (aperçu gardé en mémoire), comptes transmis montrés une fois, SMS du mot de passe
        builder.Services.AddSingleton<ApercusImport>();
        builder.Services.AddSingleton<ComptesAffiches>();
        builder.Services.AddSingleton(options.SmsMotDePasse ?? new Pharma.Shared.EnvoiSmsMotDePasseIndisponible());
        builder.Services.AddSingleton(reglages);
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
        // Script central 040 : seul le dépôt du fichier d'import (éditeur, contrôle d'accès passé) peut dépasser 16 Ko, AVANT la lecture
        // du formulaire par l'anti-falsification
        app.Use((ctx, suite) =>
        {
            if (HttpMethods.IsPost(ctx.Request.Method) && ctx.Request.Path.Equals(ActionsImportComptes.RouteApercu, StringComparison.OrdinalIgnoreCase)
                && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } taille)
                taille.MaxRequestBodySize = ActionsImportComptes.TailleRequeteMax;
            return suite(ctx);
        });
        app.UseAntiforgery();
        // Jeton anti-falsification absent ou faux (toutes les actions l'exigent) : 400 et page d'erreur fixe, avant toute action ; une
        // action qui lit le formulaire elle-même lèverait sinon une erreur 500
        app.Use((ctx, suite) =>
        {
            if (ctx.Features.Get<Microsoft.AspNetCore.Antiforgery.IAntiforgeryValidationFeature>() is { IsValid: false })
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }
            return suite(ctx);
        });
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
