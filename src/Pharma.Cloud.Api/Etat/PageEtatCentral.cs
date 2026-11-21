using System.Net;
using Npgsql;

namespace Pharma.Cloud.Api.Etat;

/// <summary>Réglage retenu pour la page d'état (adresse toujours 127.0.0.1).</summary>
public sealed record ReglagePageEtat(IPAddress Adresse, int Port, string Connexion);

/// <summary>
/// Page d'état du central, OUTIL DE DÉVELOPPEMENT (pas un écran S7) : second serveur web SÉPARÉ, dans le même processus que le
/// serveur central, en HTTP sur 127.0.0.1 seulement (Cloud:PortPageEtat, 5092 par défaut). Démarré seulement si l'environnement
/// est Development ET Cloud:PageEtat vaut true ET PHARMA_CLOUD_ETAT_CONNEXION est posée (rôle pharma_cloud_etat, script 029).
/// Deux routes en lecture (GET / et GET /etat.json), aucune route /api/v1, aucune écriture. Le serveur principal (5090, mTLS)
/// n'en dépend pas : un refus ou un échec de la page est journalisé et le central démarre quand même.
/// </summary>
public static class PageEtatCentral
{
    public const string VariableConnexion = "PHARMA_CLOUD_ETAT_CONNEXION";
    public const int PortParDefaut = 5092;

    /// <summary>
    /// Décide si la page démarre : (réglage, null) si oui ; (null, motif) sinon. Motif null quand la page n'est simplement pas demandée.
    /// Toute adresse autre que 127.0.0.1 (Cloud:AdressePageEtat) est refusée.
    /// </summary>
    public static (ReglagePageEtat? Reglage, string? Refus) Evaluer(bool developpement, IConfiguration configuration, string? connexion)
    {
        if (!developpement || !configuration.GetValue("Cloud:PageEtat", false)) return (null, null);
        var adresse = configuration["Cloud:AdressePageEtat"] is { Length: > 0 } a ? a.Trim() : "127.0.0.1";
        if (adresse != "127.0.0.1")
            return (null, $"Page d'état refusée : adresse « {adresse} » interdite (127.0.0.1 seulement).");
        var port = configuration.GetValue("Cloud:PortPageEtat", PortParDefaut);
        if (port is < 1024 or > 65535 || port == configuration.GetValue("Cloud:Port", 5090))
            return (null, $"Page d'état refusée : port {port} invalide (1024 à 65535, différent du port du central).");
        if (string.IsNullOrWhiteSpace(connexion))
            return (null, $"Page d'état non démarrée : variable {VariableConnexion} absente (rôle pharma_cloud_etat, scripts/dev/preparer_central_vs.ps1 -Preparer).");
        return (new ReglagePageEtat(IPAddress.Loopback, port, connexion), null);
    }

    /// <summary>Construit le serveur de la page (non démarré). Port 0 permis pour les tests (port libre choisi par le système).</summary>
    public static WebApplication Construire(IPAddress adresse, int port, ILecteurEtatCentral lecteur)
    {
        if (!adresse.Equals(IPAddress.Loopback)) throw new ArgumentException("Page d'état : 127.0.0.1 seulement.", nameof(adresse));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ApplicationName = typeof(PageEtatCentral).Assembly.GetName().Name });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(adresse, port);
            k.Limits.MaxRequestBodySize = 1024;
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddSingleton(lecteur);
        var app = builder.Build();

        // Hôte attendu seulement (protection contre la reliaison DNS) ; en-têtes de sécurité ; corps de requête limité à 1 Kio (jamais lu)
        app.Use(async (ctx, suite) =>
        {
            var hote = ctx.Request.Host.Host;
            if (hote is not ("127.0.0.1" or "localhost"))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers.XContentTypeOptions = "nosniff";
            ctx.Response.Headers.XFrameOptions = "DENY";
            ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'unsafe-inline'; form-action 'none'; frame-ancestors 'none'";
            ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
            await suite();
        });

        app.MapGet("/", async (ILecteurEtatCentral l, CancellationToken ct) =>
        {
            var (etat, erreur) = await LireAsync(l, ct);
            return Results.Content(RenduPageEtat.Html(etat, erreur, DateTimeOffset.UtcNow), "text/html; charset=utf-8");
        });
        app.MapGet("/etat.json", async (ILecteurEtatCentral l, CancellationToken ct) =>
        {
            var (etat, erreur) = await LireAsync(l, ct);
            return etat is not null ? Results.Json(etat) : Results.Problem(erreur, statusCode: StatusCodes.Status503ServiceUnavailable);
        });
        return app;
    }

    /// <summary>
    /// Appelé par Program.cs : démarre la page si elle est demandée et permise ; sinon rien (motif journalisé). Ne lève jamais : un échec
    /// (port occupé…) est journalisé et le serveur principal continue.
    /// </summary>
    public static async Task<WebApplication?> DemarrerSiDemandeeAsync(bool developpement, IConfiguration configuration, string? connexion, ILogger journal)
    {
        var (reglage, refus) = Evaluer(developpement, configuration, connexion);
        if (reglage is null)
        {
            if (refus is not null) journal.LogWarning("{Refus}", refus);
            return null;
        }
        WebApplication? page = null;
        try
        {
            var lecteur = new LecteurEtatCentral(NpgsqlDataSource.Create(reglage.Connexion), TimeProvider.System);
            page = Construire(reglage.Adresse, reglage.Port, lecteur);
            await page.StartAsync();
            journal.LogInformation("Page d'état du central (développement) : http://127.0.0.1:{Port}/", reglage.Port);
            return page;
        }
        catch (Exception ex)
        {
            journal.LogError("Page d'état du central non démarrée : {Message}", ex.Message);
            if (page is not null) await page.DisposeAsync();
            return null;
        }
    }

    private static async Task<(EtatCentral? Etat, string? Erreur)> LireAsync(ILecteurEtatCentral lecteur, CancellationToken ct)
    {
        try
        {
            return (await lecteur.LireAsync(ct), null);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            return (null, ex is Npgsql.PostgresException pg ? $"{pg.SqlState} : {pg.MessageText}" : ex.Message);
        }
    }
}
