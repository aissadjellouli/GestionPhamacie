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

// HTTPS pour le scanner mobile (caméra du téléphone) : fichier écrit par scripts/certificat_mobile.ps1
builder.Configuration.AddJsonFile("appsettings.Https.json", optional: true, reloadOnChange: false);

// Clé de signature des jetons : la valeur d'exemple n'est tolérée que sur un poste de développement
JetonService.VerifierCle(builder.Configuration, builder.Environment.IsDevelopment());

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
    builder.Configuration["SuperAdmin:Fichier"]));
builder.Services.AddSingleton<JetonService>();
builder.Services.AddSingleton<IGenerateurJeton>(sp => sp.GetRequiredService<JetonService>());
// Connexion par code comptoir : droits limités au comptoir (enveloppe le service de droits)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Pharma.Infrastructure.Securite.PermissionService>();
builder.Services.AddScoped<Pharma.Api.Securite.AutorisationParCode>();
builder.Services.AddScoped<IPermissionService, PermissionsComptoir>();
builder.Services.AddScoped(sp => new Pharma.Application.Securite.ListerValidateurs(
    sp.GetRequiredService<Pharma.Application.Securite.ICodesComptoir>(), sp.GetRequiredService<Pharma.Infrastructure.Securite.PermissionService>()));
builder.Services.AddHostedService<AlertesQuotidiennes>();
builder.Services.AddHostedService<EnvoiNotifications>();
builder.Services.AddHostedService<SauvegardesAutomatiques>();
builder.Services.AddHostedService<SurveillancePeriodique>();
builder.Services.AddProblemDetails();

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

// Règles métier et refus de la base (déclencheurs) renvoyés au poste avec un message clair
app.UseExceptionHandler(erreur => erreur.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (code, message) = ex switch
    {
        RegleMetierException r => (StatusCodes.Status422UnprocessableEntity, r.Message),
        Pharma.Application.Commun.DroitRefuseException d => (StatusCodes.Status403Forbidden, d.Message),
        PostgresException { SqlState: "P0001" } p => (StatusCodes.Status422UnprocessableEntity, p.MessageText),
        PostgresException p when ErreursMetier.Traduire(p) is { } refus => refus,
        BadHttpRequestException b => (b.StatusCode, "Requête invalide : données mal formées."),
        _ => (StatusCodes.Status500InternalServerError, "Erreur interne : voir le journal du serveur.")
    };
    ctx.Response.StatusCode = code;
    await ctx.Response.WriteAsJsonAsync(new ErreurDto(message, (ex as RegleMetierException)?.Champ));
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
            .LogInformation("{Chemin} refusé (requête mal formée) : {Message}", ctx.Request.Path, b.Message);
        ctx.Response.StatusCode = b.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new ErreurDto(message));
    }
});

// Page du scanner mobile (wwwroot/mobile) : fichiers statiques, sans données
app.Use((ctx, suite) =>
{
    if (ctx.Request.Path.Value == "/mobile") { ctx.Response.Redirect("/mobile/"); return Task.CompletedTask; }
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
