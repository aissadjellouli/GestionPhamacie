using System.Globalization;
using System.Net.Http;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Pharma.Desktop.Services;

namespace Pharma.Desktop;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // Dates et montants à la française (« 52 900 DA », « dimanche 8 novembre 2026 »)
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.DefaultThreadCurrentCulture = fr;
        CultureInfo.DefaultThreadCurrentUICulture = fr;

        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        // Adresse de l'API sur le PC serveur de l'officine (variable d'environnement PHARMA_API)
        var api = Environment.GetEnvironmentVariable("PHARMA_API") ?? "http://localhost:5080/";
        services.AddSingleton(new HttpClient { BaseAddress = new Uri(api) });
        services.AddSingleton<ConfigurationPoste>();
        services.AddSingleton<ImprimanteEtiquettes>();
        services.AddSingleton<SessionPoste>();
        services.AddSingleton<ApiClient>();

        Resources.Add("services", services.BuildServiceProvider());
        base.OnStartup(e);
    }
}
