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
        // Relancé en administrateur pour créer la clé du poste (« --creer-cle-poste <nom> », D4) : rien d'autre, aucune fenêtre
        if (CreationCleAdministrateur.Traiter(e.Args, CleMachineCng.CreerEnAdministrateur) is int codeCle) Environment.Exit(codeCle);

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
        // Adresse réglée dans poste.json (AdresseApi : bascule du poste sur le PC de secours) prioritaire sur PHARMA_API
        var reglages = new ReglagesPoste();
        var api = ReglagesPoste.AdresseApiDemarrage(reglages.Actuelles, Environment.GetEnvironmentVariable("PHARMA_API"));
        // Liste des postes autorisés : identité du poste enrôlé (clé privée non exportable de Windows) et heure du serveur ; chaque requête est signée
        var identite = new IdentitePoste();
        var horlogeServeur = new HorlogePoste();
        // Chaque appel nomme le poste : il identifie la caisse (le tiroir) partagée par les employés du poste (nom donné par le titulaire si le poste est enrôlé)
        var poste = new ConfigurationPoste(identite.Nom);
        // Poste renommé par le titulaire : le nom reçu du serveur (identité mise à jour) remplace celui affiché, sans redémarrage
        identite.Changee += () => poste.Renommer(identite.Nom);
        // Chaque appel est compté : la barre de chargement du haut de la fenêtre s'affiche pendant les appels
        var appels = new AppelsEnCours();
        services.AddSingleton(appels);
        // Délai d'abandon de chaque appel : celui de SuiviAppels (30 s, 2 min ou 10 min selon l'opération), pas les 100 s par défaut
        // Bascule sur le 2e serveur sans redémarrage (lot V2-6) : le client garde son adresse de départ, RelaisAdresse réécrit chaque requête
        // vers l'adresse courante ; SurveillanceEcart confie à la vigie les réponses 503 « serveur_ecarte » avant de les rendre à l'appelant
        var session = new SessionPoste();
        var panier = new PanierBascule();
        var adresseCourante = new AdresseApiCourante(new Uri(api));
        var version = typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor:00}" : "?";
        vigie = new VigiePoste(reglages, session, adresseCourante, new ReseauVigieHttp(poste.Poste,
            new HttpClient(new SignaturePoste(identite, horlogeServeur, new HttpClientHandler())) { Timeout = TimeSpan.FromSeconds(4) },
            () => session.EstConnecte ? session.Jeton : null), panier, poste.Poste,
            Environment.GetEnvironmentVariable("PHARMA_API"), version, journal: NoterErreur);
        var chaine = new SurveillanceEcart(vigie, new RelaisAdresse(adresseCourante, new SignaturePoste(identite, horlogeServeur, new SuiviAppels(appels))));
        var client = new HttpClient(chaine) { BaseAddress = new Uri(api), Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.Add("X-Pharma-Poste", Uri.EscapeDataString(poste.Poste));
        services.AddSingleton(client);
        services.AddSingleton(poste);
        services.AddSingleton(identite);
        services.AddSingleton(horlogeServeur);
        services.AddSingleton(reglages);
        services.AddSingleton(session);
        services.AddSingleton(panier);
        services.AddSingleton(adresseCourante);
        services.AddSingleton(vigie);
        services.AddSingleton<IBasculePoste>(vigie);
        services.AddSingleton<ImprimanteEtiquettes>();
        services.AddSingleton<ImprimanteTickets>();
        services.AddSingleton<AfficheurClient>();
        services.AddSingleton<ApiClient>();
        services.AddSingleton<OffrePoste>();
        services.AddSingleton<FichesContact>();
        // Compteurs du travail en attente (menu du haut et menu de gauche)
        services.AddSingleton<CompteursAttente>();
        // Rubriques repliées ou dépliées dans les menus de gauche, gardées tant que le poste est ouvert (GroupeMenu)
        services.AddSingleton<EtatMenus>();
        // Tâches laissées au « Changer d'utilisateur » (facture, retour, réception), reprises par le suivant sur ce poste ;
        // facture et retour gardés dans %LOCALAPPDATA%\PharmaOfficine\taches-laissees.dat (chiffré DPAPI) : survivent au redémarrage.
        // Réglage de l'officine (titulaire, script 156) lu à chaque connexion par ApiClient : « Désactivé » = mémoire seule, fichier supprimé ;
        // tant qu'il n'est pas lu (avant la première connexion), le fichier n'est ni lu, ni écrit, ni supprimé
        services.AddSingleton(_ => new TachesLaissees(new FichierTachesLaissees(FichierTachesLaissees.DossierParDefaut())));
        // Historique des écrans pour « ← Retour » (un par fenêtre du poste)
        services.AddScoped<HistoriqueNavigation>();
        services.AddSingleton<DemandesAutorisation>();
        // « Rembourser » le trop-payé d'une régularisation Chifa : demande reprise par la colonne d'actions de la caisse (2026-11-15)
        services.AddSingleton<RemboursementsTiroir>();

        // Dernier filet : une erreur imprévue hors d'un écran est notée et signalée, sans arrêter le poste en pleine vente
        DispatcherUnhandledException += (_, args) =>
        {
            NoterErreur(args.Exception);
            MessageBox.Show(
                "Une erreur imprévue s’est produite. Ce qui était déjà enregistré sur le serveur le reste : vérifiez avant de recommencer.\n\n"
                + $"{args.Exception.GetType().Name} : {args.Exception.Message}",
                "Tracodex Officine", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };

        fournisseur = services.BuildServiceProvider();
        // Vigie du poste : rapport au 2e serveur toutes les 15 s (rien du tout sans adresse de secours : un seul serveur)
        vigie.Demarrer();
        Resources.Add("services", fournisseur);
        base.OnStartup(e);
    }

    private ServiceProvider? fournisseur;
    private VigiePoste? vigie;

    /// <summary>
    /// Fermeture du logiciel : les services créés sont libérés, dont l'afficheur client (port série refermé, fenêtre client fermée)
    /// pour que le port soit libre au prochain démarrage.
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        vigie?.Dispose();
        try { fournisseur?.Dispose(); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.IO.IOException) { NoterErreur(ex); }
        fournisseur = null;
        base.OnExit(e);
    }

    /// <summary>
    /// Journal local des erreurs imprévues du poste : erreurs.log du dossier des journaux du poste (Services.JournauxPoste).
    /// Message et pile masqués (téléphones, e-mails, n° d'assuré) : aucune donnée personnelle au journal.
    /// </summary>
    internal static void NoterErreur(Exception ex)
    {
        try
        {
            var dossier = Pharma.Desktop.Services.JournauxPoste.Dossier;
            System.IO.Directory.CreateDirectory(dossier);
            System.IO.File.AppendAllText(System.IO.Path.Combine(dossier, "erreurs.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {Pharma.Shared.MasqueurJournal.Masquer(ex)}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { }
    }
}
