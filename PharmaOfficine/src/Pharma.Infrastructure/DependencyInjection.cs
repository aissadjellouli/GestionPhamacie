using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pharma.Application.Alertes;
using Pharma.Application.Catalogue;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Stock;
using Pharma.Application.Tracabilite;
using Pharma.Infrastructure.Alertes;
using Pharma.Infrastructure.Chifa;
using Pharma.Infrastructure.Impression;
using Pharma.Infrastructure.Lecture;
using Pharma.Infrastructure.Persistence;
using Pharma.Infrastructure.Securite;
using Pharma.Infrastructure.Systeme;
using Pharma.Infrastructure.Tracabilite;

namespace Pharma.Infrastructure;

public sealed record InfrastructureOptions(string ChaineConnexion, string DossierExportChifa);

public static class DependencyInjection
{
    /// <summary>Adaptateurs des ports de l'Application : PostgreSQL, sécurité, horloge, Chifa, impression.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, InfrastructureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ChaineConnexion))
            throw new InvalidOperationException("Chaîne de connexion « Pharmacie » manquante.");

        services.AddSingleton(NpgsqlDataSource.Create(options.ChaineConnexion));
        services.AddDbContext<PharmaDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<IHorloge, HorlogeSysteme>();
        services.AddSingleton<IVerificateurMotDePasse, VerificateurBcrypt>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IComptesLecture, ComptesLecture>();
        services.AddScoped<ICatalogueLecture, CatalogueLecture>();
        services.AddScoped<ILotsLecture, LotsLecture>();
        services.AddScoped<IAlternativesLecture, AlternativesLecture>();
        services.AddScoped<IStockLecture, StockLecture>();
        services.AddScoped<IEtiquetageLecture, EtiquetageLecture>();
        services.AddScoped<IAlertesLecture, AlertesLecture>();
        services.AddScoped<IMoteurAlertes, MoteurAlertesSql>();
        services.AddScoped<IAlertesEcriture, AlertesEcriture>();
        services.AddScoped<IRappelLot, RappelLotSql>();
        services.AddScoped<ITracabiliteLecture, TracabiliteLecture>();
        services.AddSingleton<CodesInternesLot>();
        services.AddSingleton<IChifaConnecteur>(sp => new ChifaFichiersConnecteur(
            options.DossierExportChifa,
            sp.GetRequiredService<ILogger<ChifaFichiersConnecteur>>()));

        return services;
    }
}
