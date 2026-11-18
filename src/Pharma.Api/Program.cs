using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Pharma.Api.Endpoints;
using Pharma.Api.Securite;
using Pharma.Api.Taches;
using Pharma.Application;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Infrastructure;
using Pharma.Shared;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var builder = WebApplication.CreateBuilder(args);

// HTTPS sur le réseau de la pharmacie (postes et téléphones) : fichier écrit par scripts/certificat_https.ps1
builder.Configuration.AddJsonFile("appsettings.Https.json", optional: true, reloadOnChange: false);

// Clé de signature des jetons : la valeur d'exemple n'est tolérée que sur un poste de développement
JetonService.VerifierCle(builder.Configuration, builder.Environment.IsDevelopment());

// HTTP en clair sur le réseau refusé hors développement, sauf réglage AutoriserHttpReseau=true (docs/mise-en-production.md)
if (ReseauSecurise.RefusDemarrage(builder.Configuration, builder.Environment.IsDevelopment()) is { } refusHttp)
    throw new InvalidOperationException(refusHttp);

// Exécution en service Windows sur le PC serveur de l'officine
builder.Host.UseWindowsService(o => o.ServiceName = "PharmaOfficine API");

// Journal du serveur en fichiers (un par jour, taille et durée de conservation réglables : section « Journaux »)
var optionsJournal = builder.Configuration.GetSection("Journaux").Get<Pharma.Api.Journalisation.OptionsJournalFichier>()
                     ?? new Pharma.Api.Journalisation.OptionsJournalFichier();
builder.Logging.AddProvider(new Pharma.Api.Journalisation.JournalFichierProvider(optionsJournal));

// GED : un dossier du serveur pour tous les documents (réglage Ged:Dossier). Les ordonnances y vont par défaut, et celles de
// l'ancien dossier par défaut y sont déplacées au démarrage (sans rien écraser). Chifa : les fichiers sont lus dans le dossier
// d'export du logiciel de la CNAS (Chifa:DossierExport) et archivés dans GED\Chifa
var dossierPharma = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine");
var ged = new Pharma.Application.Ged.ReglagesGed(
    builder.Configuration["Ged:Dossier"] is { Length: > 0 } g ? g : Path.Combine(dossierPharma, "GED"),
    builder.Configuration.GetValue("Ged:RetentionSauvegardesJours", 7));
var dossierOrdonnances = builder.Configuration["Ordonnances:Dossier"] is { Length: > 0 } o ? o : ged.Dossier(Pharma.Application.Ged.TypesGed.Ordonnances);
var dossierChifa = builder.Configuration["Chifa:DossierExport"] is { Length: > 0 } c ? c : ged.Dossier(Pharma.Application.Ged.TypesGed.Chifa);
// 2e serveur (script 122) : rôle de la base de ce serveur, dossiers propres à ce serveur (Chifa, MDN : sa ligne de serveur_officine
// l'emporte sur appsettings), autre serveur de l'officine (SecondServeur:AutreServeur) pour ne jamais avoir deux API qui écrivent
var demarrage = await Pharma.Infrastructure.SecondServeur.DemarrageServeur.LireAsync(
    builder.Configuration.GetConnectionString("Pharmacie") ?? "", builder.Configuration["SecondServeur:AutreServeur"]);
dossierChifa = Pharma.Application.SecondServeur.DossiersServeur.Choisir(demarrage.DossierChifa, dossierChifa) ?? dossierChifa;
var ceServeur = new Pharma.Application.SecondServeur.CeServeur(Pharma.Infrastructure.SecondServeur.DemarrageServeur.NomCeServeur, dossierChifa,
    Pharma.Application.SecondServeur.DossiersServeur.Choisir(demarrage.DossierMdn, builder.Configuration["Mdn:Dossier"]),
    typeof(Program).Assembly.GetName().Version is { } va ? (va.Build > 0 ? $"{va.Major}.{va.Minor:00}.{va.Build}" : $"{va.Major}.{va.Minor:00}") : null);
builder.Services.AddSingleton(ceServeur);
// Chaque dossier à part : un dossier réglé ailleurs et introuvable (ex. Chifa sur un disque absent) n'empêche pas les autres
foreach (var d in new[] { dossierOrdonnances, ged.Dossier(Pharma.Application.Ged.TypesGed.Chifa), ged.Dossier(Pharma.Application.Ged.TypesGed.Sauvegardes) })
    try { Directory.CreateDirectory(d); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"GED : dossier {d} inaccessible ({e.Message})"); }
try
{
    var deplaces = Pharma.Infrastructure.Ged.GedFichiers.Deplacer(Path.Combine(dossierPharma, "Ordonnances"), dossierOrdonnances);
    if (deplaces > 0) Console.WriteLine($"GED : {deplaces} ordonnance(s) déplacée(s) dans {dossierOrdonnances}");
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.WriteLine($"GED : ordonnances non déplacées dans {dossierOrdonnances} ({e.Message})");
}
builder.Services.AddSingleton(ged);

// Composition : cas d'usage, adaptateurs, services propres à l'hôte
builder.Services.AddApplication();
builder.Services.AddInfrastructure(new InfrastructureOptions(
    builder.Configuration.GetConnectionString("Pharmacie") ?? "",
    dossierChifa,
    dossierOrdonnances,
    builder.Configuration["Sauvegardes:PgDump"],
    ged.Dossier(Pharma.Application.Ged.TypesGed.Chifa),
    builder.Configuration["SuperAdmin:Fichier"],
    builder.Configuration["Integrite:FichierCle"],
    builder.Configuration["Synchronisation:Dossier"]));
builder.Services.AddSingleton<JetonService>();
builder.Services.AddSingleton<IGenerateurJeton>(sp => sp.GetRequiredService<JetonService>());
// Connexion par code comptoir : droits limités au comptoir (enveloppe le service de droits)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Pharma.Infrastructure.Securite.PermissionService>();
builder.Services.AddScoped<Pharma.Api.Securite.AutorisationParCode>();
builder.Services.AddScoped<IPermissionService, PermissionsComptoir>();
builder.Services.AddScoped(sp => new Pharma.Application.Securite.ListerValidateurs(
    sp.GetRequiredService<Pharma.Application.Securite.ICodesComptoir>(), sp.GetRequiredService<Pharma.Infrastructure.Securite.PermissionService>()));
// Synchronisation, lot S1 : mise en forme sync-v1 des lots (aucun envoi, aucune adresse de serveur central)
builder.Services.AddSingleton<Pharma.Application.Synchronisation.IFormatLotSync, Pharma.Api.Synchronisation.FormatLotSyncV1>();
// Synchronisation, lot S2 : envoi au serveur central (adresse réglée en base ; rien ne part tant que désactivée ou non activée)
builder.Services.AddSingleton<Pharma.Api.Synchronisation.ClientServeurCentralHttp>();
builder.Services.AddSingleton<Pharma.Application.Synchronisation.IClientServeurCentral>(sp => sp.GetRequiredService<Pharma.Api.Synchronisation.ClientServeurCentralHttp>());
builder.Services.AddSingleton<Pharma.Application.Synchronisation.IClientChargementCentral>(sp => sp.GetRequiredService<Pharma.Api.Synchronisation.ClientServeurCentralHttp>());
builder.Services.AddScoped<Pharma.Application.Synchronisation.ITelechargementReferentiel, Pharma.Api.Synchronisation.TelechargementReferentielHttp>();
builder.Services.AddHostedService<Pharma.Api.Taches.EnvoiSynchronisation>();
// Synchronisation, lot S3 : alertes au titulaire (retard, erreur persistante, certificat, quota) et purge de la file après accusé
builder.Services.AddHostedService<Pharma.Api.Taches.SurveillanceSynchronisation>();
builder.Services.AddHostedService<Pharma.Api.Taches.PurgeSynchronisation>();
builder.Services.AddHostedService<AlertesQuotidiennes>();
builder.Services.AddHostedService<EnvoiNotifications>();
builder.Services.AddHostedService<SauvegardesAutomatiques>();
builder.Services.AddHostedService<SurveillancePeriodique>();
builder.Services.AddHostedService<VerificationIntegriteNocturne>();
builder.Services.AddHostedService<ScellementMensuelRegistre>();
builder.Services.AddHostedService<ClotureFiscaleMensuelle>();
builder.Services.AddHostedService<SerialisationHorsLigne>();
builder.Services.AddHostedService<SurveillanceSecondServeur>();
builder.Services.AddProblemDetails();
// Limite de débit par adresse IP sur les routes de connexion (mots de passe, codes comptoir, super admin)
builder.Services.AjouterLimiteConnexions(builder.Configuration);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = builder.Configuration["Jwt:Emetteur"],
            ValidAudience = builder.Configuration["Jwt:Emetteur"],
            IssuerSigningKey = JetonService.Cle(builder.Configuration),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        // Compte désactivé, mot de passe changé, code effacé ou base restaurée : connexion refusée à l'appel suivant
        o.Events = new JwtBearerEvents { OnTokenValidated = ValidationJeton.VerifierAsync };
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Création ou modification du compte super admin sur le PC serveur : Pharma.Api.exe --super-admin (le serveur ne démarre pas)
if (args.Contains(ConsoleSuperAdmin.Argument))
    return await ConsoleSuperAdmin.ExecuterAsync(app.Services);

// Clé de scellement sur le 2e serveur : Pharma.Api.exe --importer-cle <fichier> | --empreinte-cle (le serveur ne démarre pas)
if (ConsoleCleScellement.Concerne(args))
    return ConsoleCleScellement.Executer(app.Services, args);

// Jamais deux API qui écrivent : base en lecture seule (2e serveur en veille) ou autre serveur principal => refus, message clair
if (Pharma.Application.SecondServeur.ReplicationOfficine.RefusDemarrage(demarrage.BaseEnRecovery, demarrage.AutreServeurPrincipal,
        builder.Configuration["SecondServeur:AutreServeur"], demarrage.Timeline, demarrage.TimelineAutre) is { } refusServeur)
{
    app.Logger.LogCritical("{Refus}", refusServeur);
    Console.Error.WriteLine(refusServeur);
    return 2;
}

// Fuseau horaire : l'officine est en UTC+1 toute l'année (Africa/Algiers) ; la base est réglée par la migration 083
if (ReseauSecurise.AvertissementFuseau(TimeZoneInfo.Local, DateTime.UtcNow) is { } fuseau)
    app.Logger.LogWarning("{Avertissement}", fuseau);

// Règles métier et refus de la base (déclencheurs) renvoyés au poste avec un message clair
app.UseExceptionHandler(erreur => erreur.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (code, message) = ex switch
    {
        RegleMetierException r => (StatusCodes.Status422UnprocessableEntity, r.Message),
        Pharma.Application.Reglages.ReglageImposeException i => (StatusCodes.Status409Conflict, i.Message),
        Pharma.Application.Reglages.ModeleReglageChangeException c => (StatusCodes.Status409Conflict, c.Message),
        Pharma.Application.Commun.DroitRefuseException d => (StatusCodes.Status403Forbidden, d.Message),
        PostgresException { SqlState: "P0001" } p => (StatusCodes.Status422UnprocessableEntity, p.MessageText),
        PostgresException p when ErreursMetier.Traduire(p) is { } refus => refus,
        BadHttpRequestException b => (b.StatusCode, "Requête invalide : données mal formées."),
        _ => (StatusCodes.Status500InternalServerError, "Erreur interne : voir le journal du serveur.")
    };
    ctx.Response.StatusCode = code;
    await ctx.Response.WriteAsJsonAsync(new ErreurDto(message,
        ex is Pharma.Application.Reglages.ReglageImposeException ? Pharma.Application.Reglages.ReglageImposeException.Champ : (ex as RegleMetierException)?.Champ));
}));

// Requête mal formée (paramètre manquant ou illisible) : refus clair, noté comme simple information au journal du serveur,
// et non comme une panne (il ne remonte plus jusqu'au gestionnaire d'erreurs ci-dessus)
app.Use(async (ctx, suite) =>
{
    try { await suite(); }
    catch (BadHttpRequestException b) when (!ctx.Response.HasStarted)
    {
        var nom = System.Text.RegularExpressions.Regex.Match(b.Message, @"parameter ""(?:\S+ )?(\w+)""") is { Success: true } m ? m.Groups[1].Value : null;
        var message = nom is null ? "Requête invalide : données mal formées." : $"Requête invalide : le paramètre « {nom} » est manquant ou mal formé.";
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Pharma.Refus")
            .LogInformation("{Chemin} refusé (requête mal formée, paramètre {Parametre})", ctx.Request.Path, nom ?? "-");
        ctx.Response.StatusCode = b.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new ErreurDto(message));
    }
});

// HTTPS configuré : HSTS et redirection des requêtes HTTP du réseau (sauf HTTP gardé volontairement : AutoriserHttpReseau)
app.UtiliserHttps();
app.UseRateLimiter();

// Page du scanner mobile (wwwroot/mobile) : fichiers statiques, sans données
app.Use((ctx, suite) =>
{
    if (ctx.Request.Path.Value == "/mobile") { ctx.Response.Redirect("/mobile/"); return Task.CompletedTask; }
    return suite();
});
app.Use((ctx, suite) =>
{
    // Certificat de l'autorité : plus distribué par le réseau (clé USB), seulement au PC serveur
    if (ReseauSecurise.CertificatAutoriteRefuse(ctx.Request.Path, ctx.Connection.RemoteIpAddress))
    {
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
    return suite();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache"
});

// Restauration de la base en cours (super admin) : les postes sont mis en attente, seules les routes du super admin répondent
app.Use(async (ctx, suite) =>
{
    var chemin = ctx.Request.Path;
    if (chemin.StartsWithSegments("/api") && !chemin.StartsWithSegments("/api/super-admin") && !chemin.StartsWithSegments("/api/sante")
        && ctx.RequestServices.GetRequiredService<Pharma.Application.SuperAdmin.RestaurationsSuperAdmin>().BaseEnCours)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await ctx.Response.WriteAsJsonAsync(new ErreurDto("Restauration de la base en cours par le super admin : patientez quelques minutes, puis reconnectez-vous."));
        return;
    }
    await suite();
});

app.UseAuthentication();
app.UseAuthorization();
app.MapPharmaEndpoints();

app.Run();
return 0;

public partial class Program;
