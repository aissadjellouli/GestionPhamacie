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
        // http:// ou https:// (HTTPS conseillé dès que le serveur est sur un autre PC : docs/mise-en-production.md ; le certificat du
        // serveur doit être reconnu par Windows). Barre finale ajoutée : les routes « api/… » sont relatives à cette adresse.
        var api = (Environment.GetEnvironmentVariable("PHARMA_API") is { Length: > 0 } a ? a.Trim() : "http://localhost:5080/");
        if (!api.EndsWith('/')) api += "/";
        // Chaque appel nomme le poste : il identifie la caisse (le tiroir) partagée par les employés du poste
        var poste = new ConfigurationPoste();
        // Chaque appel est compté : la barre de chargement du haut de la fenêtre s'affiche pendant les appels
        var appels = new AppelsEnCours();
        services.AddSingleton(appels);
        // Délai d'abandon de chaque appel : celui de SuiviAppels (30 s, 2 min ou 10 min selon l'opération), pas les 100 s par défaut
        var client = new HttpClient(new SuiviAppels(appels)) { BaseAddress = new Uri(api), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("X-Pharma-Poste", Uri.EscapeDataString(poste.Poste));
        services.AddSingleton(client);
        services.AddSingleton(poste);
        services.AddSingleton<ReglagesPoste>();
        services.AddSingleton<ImprimanteEtiquettes>();
        services.AddSingleton<ImprimanteTickets>();
        services.AddSingleton<AfficheurClient>();
        services.AddSingleton<SessionPoste>();
        services.AddSingleton<ApiClient>();
        services.AddSingleton<FichesContact>();
        // Compteurs du travail en attente (menu du haut et menu de gauche)
        services.AddSingleton<CompteursAttente>();
        // Tâches laissées au « Changer d'utilisateur » (facture, retour, réception), reprises par le suivant sur ce poste
        services.AddSingleton<TachesLaissees>();
        // Historique des écrans pour « ← Retour » (un par fenêtre du poste)
        services.AddScoped<HistoriqueNavigation>();
        services.AddSingleton<DemandesAutorisation>();

        // Dernier filet : une erreur imprévue hors d'un écran est notée et signalée, sans arrêter le poste en pleine vente
        DispatcherUnhandledException += (_, args) =>
        {
            NoterErreur(args.Exception);
            MessageBox.Show(
                "Une erreur imprévue s’est produite. Ce qui était déjà enregistré sur le serveur le reste : vérifiez avant de recommencer.\n\n"
                + $"{args.Exception.GetType().Name} : {args.Exception.Message}",
                "Pharma Officine", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        fournisseur = services.BuildServiceProvider();
        Resources.Add("services", fournisseur);
        base.OnStartup(e);
    }

    private ServiceProvider? fournisseur;

    /// <summary>
    /// Fermeture du logiciel : les services créés sont libérés, dont l'afficheur client (port série refermé, fenêtre client fermée)
    /// pour que le port soit libre au prochain démarrage.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try { fournisseur?.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException) { NoterErreur(ex); }
        fournisseur = null;
        base.OnExit(e);
    }

    /// <summary>Journal local des erreurs imprévues du poste : %LOCALAPPDATA%\PharmaOfficine\erreurs.log.</summary>
    private static void NoterErreur(Exception ex)
    {
        try
        {
            var dossier = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PharmaOfficine");
            System.IO.Directory.CreateDirectory(dossier);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dossier, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
