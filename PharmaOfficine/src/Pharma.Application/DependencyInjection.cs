using Microsoft.Extensions.DependencyInjection;
using Pharma.Application.Alertes;
using Pharma.Application.Catalogue;
using Pharma.Application.Securite;
using Pharma.Application.Stock;
using Pharma.Application.Tracabilite;

namespace Pharma.Application;

public static class DependencyInjection
{
    /// <summary>Cas d'usage. Les ports qu'ils utilisent sont fournis par l'Infrastructure et l'Api.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<SeConnecter>();
        services.AddScoped<RechercherProduits>();
        services.AddScoped<ObtenirLotFefo>();
        services.AddScoped<ListerAlternatives>();
        services.AddScoped<ConsulterStock>();
        services.AddScoped<ObtenirEtatEtiquetage>();
        services.AddScoped<ListerAlertesOuvertes>();
        services.AddScoped<GenererAlertes>();
        services.AddScoped<ReporterAlerte>();
        services.AddScoped<TraiterAlerte>();
        services.AddScoped<RappelerLot>();
        services.AddScoped<ConsulterParcoursBoite>();
        return services;
    }
}
