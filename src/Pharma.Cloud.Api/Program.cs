using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Npgsql;
using Pharma.Cloud.Api;
using Pharma.Cloud.Api.Reception;
using Pharma.Cloud.Api.Securite;
using Pharma.Shared;

// Serveur central de synchronisation (lot S2, docs/conception-synchronisation.md § 12) : HTTPS + certificat client (mTLS),
// jeton court ES256 lié au certificat, dépôt des lots sync-v1 par officine. Secrets : variables d'environnement seulement.
Console.OutputEncoding = System.Text.Encoding.UTF8;
if (ConsoleAdministration.Concerne(args)) return await ConsoleAdministration.ExecuterAsync(args);

var builder = WebApplication.CreateBuilder(args);
// Journal en fichiers (section « Journaux » du appsettings.json : dossier, taille maximale, durée de conservation, niveau)
// Fourni par le conteneur : l'hôte l'arrête par DisposeAsync, sans attente bloquante (convention du 2026-11-28)
var optionsJournalCentral = Pharma.Shared.Journalisation.OptionsJournalFichier.Lire(cle => builder.Configuration["Journaux:" + cle], "central-api-");
builder.Services.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(_ => new Pharma.Shared.Journalisation.JournalFichierProvider(optionsJournalCentral));
var dossier = ConsoleAdministration.Dossier(args);
var motDePasse = ConsoleAdministration.MotDePasse();
var connexion = Environment.GetEnvironmentVariable("PHARMA_CLOUD_CONNEXION") is { Length: > 0 } cx ? cx
    : throw new InvalidOperationException("Variable PHARMA_CLOUD_CONNEXION absente (rôle pharma_cloud_api).");
var autorite = AutoriteCertification.Charger(dossier, motDePasse);
var certificatServeur = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(dossier, AutoriteCertification.FichierServeur), motDePasse);

builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = ControleLot.TailleMax;
    var port = builder.Configuration.GetValue("Cloud:Port", 5090);
    var adresse = builder.Configuration["Cloud:Adresse"] is { Length: > 0 } a ? IPAddress.Parse(a) : IPAddress.Loopback;
    k.Listen(adresse, port, l => l.UseHttps(h =>
    {
        h.ServerCertificate = certificatServeur;
        h.ServerCertificateChain = [autorite.Racine];
        h.SslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13;
        // Certificat client permis (l'activation n'en a pas encore) ; vérifié ensuite contre l'autorité et la base à chaque appel
        h.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
        h.ClientCertificateValidation = (_, _, _) => true;
    }));
});

builder.Services.AddSingleton(autorite);
builder.Services.AddSingleton(new JetonsCentral(JetonsCentral.ChargerOuCreerCle(dossier, motDePasse)));
builder.Services.AddSingleton(NpgsqlDataSource.Create(connexion));
builder.Services.AddSingleton<DepotCentral>();
builder.Services.AddSingleton(sp => new SuiviChargementCentral(sp.GetRequiredService<DepotCentral>().ChargementEnCoursAsync, TimeProvider.System));
var lotsParMinute = builder.Configuration.GetValue("Cloud:LotsParMinute", 60);
var lotsParMinuteChargement = builder.Configuration.GetValue("Cloud:LotsParMinuteChargement", 600);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddProblemDetails();
var limiteAdresse = builder.Configuration.GetValue("Cloud:LimiteParAdresseMinute", 600);
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Clé : certificat SEULEMENT s'il a été vérifié contre l'autorité (Cle), sinon adresse ; plus une limite globale par adresse.
    // Derrière un proxy inverse, l'adresse vue est celle du proxy (X-Forwarded-For non lu) : [À VÉRIFIER] à l'hébergement.
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
        "ip:" + ctx.Connection.RemoteIpAddress, _ => new FixedWindowRateLimiterOptions { PermitLimit = limiteAdresse, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy("officine", ctx => RateLimitPartition.GetTokenBucketLimiter(Cle(ctx),
        _ => new TokenBucketRateLimiterOptions { TokenLimit = 30, TokensPerPeriod = 30, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
    // Dépôts : limite relevée pendant un chargement complet annoncé et non terminé (seau distinct, script 006)
    o.AddPolicy("lots", ctx =>
    {
        var (cle, limite) = SuiviChargementCentral.Politique(Cle(ctx), ctx.Items[ChargementEnCours] is true, lotsParMinute, lotsParMinuteChargement);
        return RateLimitPartition.GetTokenBucketLimiter(cle,
            _ => new TokenBucketRateLimiterOptions { TokenLimit = limite, TokensPerPeriod = limite, ReplenishmentPeriod = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true });
    });
    // Renouvellement : une fois par jour par (officine, serveur) = sujet imposé du certificat
    o.AddPolicy("renouvellement", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Items[CertificatVerifie] is X509Certificate2 c ? "sujet:" + c.Subject : "ip:" + ctx.Connection.RemoteIpAddress,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 1, Window = TimeSpan.FromDays(1), QueueLimit = 0 }));
    o.AddPolicy("activation", ctx => RateLimitPartition.GetFixedWindowLimiter("ip:" + ctx.Connection.RemoteIpAddress,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
// Corps limité à quelques Kio hors dépôt de lots ; certificat client vérifié contre l'autorité AVANT la limite de débit
app.Use(async (ctx, suite) =>
{
    var chemin = ctx.Request.Path;
    if (!chemin.StartsWithSegments("/api/v1/lots")
        && ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } taille)
        taille.MaxRequestBodySize = 16 * 1024;
    // Même horloge que PointsEntree.CertificatAsync, qui réutilise ce résultat (chaîne X509 construite une seule fois par appel)
    if (ctx.Connection.ClientCertificate is { } c && autorite.ValiderClient(c, ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()))
    {
        ctx.Items[CertificatVerifie] = c;
        if (chemin.StartsWithSegments("/api/v1/lots"))
            ctx.Items[ChargementEnCours] = await ctx.RequestServices.GetRequiredService<SuiviChargementCentral>().EnCoursAsync(AutoriteCertification.Empreinte(c), ctx.RequestAborted);
    }
    await suite();
});
app.UseRateLimiter();
app.MapServeurCentral();
app.Logger.LogInformation("Serveur central prêt ; autorité {Empreinte}", autorite.EmpreinteRacine[..16]);
// Page d'état de DÉVELOPPEMENT (serveur séparé, http://127.0.0.1:5092) : seulement en Development avec Cloud:PageEtat=true et
// PHARMA_CLOUD_ETAT_CONNEXION (rôle pharma_cloud_etat). Sans effet sur ce serveur : refus ou échec journalisés, jamais bloquants.
var pageEtat = await Pharma.Cloud.Api.Etat.PageEtatCentral.DemarrerSiDemandeeAsync(builder.Environment.IsDevelopment(), builder.Configuration,
    Environment.GetEnvironmentVariable(Pharma.Cloud.Api.Etat.PageEtatCentral.VariableConnexion), app.Logger);
await app.RunAsync();
if (pageEtat is not null)
{
    await pageEtat.StopAsync();
    await pageEtat.DisposeAsync();
}
return 0;

public partial class Program
{
    internal const string CertificatVerifie = "certificat-verifie";
    internal const string ChargementEnCours = "chargement-en-cours";

    private static string Cle(HttpContext ctx) =>
        ctx.Items[CertificatVerifie] is X509Certificate2 c ? "cert:" + AutoriteCertification.Empreinte(c) : "ip:" + ctx.Connection.RemoteIpAddress;
}
