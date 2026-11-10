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
        // Chaque appel nomme le poste : il identifie la caisse (le tiroir) partagée par les employés du poste
        var poste = new ConfigurationPoste();
        var client = new HttpClient { BaseAddress = new Uri(api) };
        client.DefaultRequestHeaders.Add("X-Pharma-Poste", Uri.EscapeDataString(poste.Poste));
        services.AddSingleton(client);
        services.AddSingleton(poste);
        services.AddSingleton<ReglagesPoste>();
        services.AddSingleton<ImprimanteEtiquettes>();
        services.AddSingleton<ImprimanteTickets>();
        services.AddSingleton<AfficheurClient>();
        services.AddSingleton<SessionPoste>();
        services.AddSingleton<ApiClient>();

        Resources.Add("services", services.BuildServiceProvider());
        base.OnStartup(e);
    }
}
