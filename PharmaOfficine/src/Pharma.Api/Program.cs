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

var builder = WebApplication.CreateBuilder(args);

// Exécution en service Windows sur le PC serveur de l'officine
builder.Host.UseWindowsService(o => o.ServiceName = "PharmaOfficine API");

// Composition : cas d'usage, adaptateurs, services propres à l'hôte
builder.Services.AddApplication();
builder.Services.AddInfrastructure(new InfrastructureOptions(
    builder.Configuration.GetConnectionString("Pharmacie") ?? "",
    builder.Configuration["Chifa:DossierExport"] ?? ""));
builder.Services.AddSingleton<IGenerateurJeton, JetonService>();
builder.Services.AddHostedService<AlertesQuotidiennes>();
builder.Services.AddProblemDetails();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o => o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = builder.Configuration["Jwt:Emetteur"],
        ValidAudience = builder.Configuration["Jwt:Emetteur"],
        IssuerSigningKey = JetonService.Cle(builder.Configuration),
        ClockSkew = TimeSpan.FromMinutes(1)
    });
builder.Services.AddAuthorization();

var app = builder.Build();

// Règles métier et refus de la base (déclencheurs) renvoyés au poste avec un message clair
app.UseExceptionHandler(erreur => erreur.Run(async ctx =>
{
    var ex = ctx.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    var (code, message) = ex switch
    {
        RegleMetierException r => (StatusCodes.Status422UnprocessableEntity, r.Message),
        PostgresException { SqlState: "P0001" } p => (StatusCodes.Status422UnprocessableEntity, p.MessageText),
        BadHttpRequestException b => (b.StatusCode, "Requête invalide : données mal formées."),
        _ => (StatusCodes.Status500InternalServerError, "Erreur interne : voir le journal du serveur.")
    };
    ctx.Response.StatusCode = code;
    await ctx.Response.WriteAsJsonAsync(new ErreurDto(message));
}));

app.UseAuthentication();
app.UseAuthorization();
app.MapPharmaEndpoints();

app.Run();

public partial class Program;
