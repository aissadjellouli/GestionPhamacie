using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pharma.Application.Alertes;
using Pharma.Application.Caisse;
using Pharma.Application.Patients;
using Pharma.Infrastructure.Caisse;
using Pharma.Infrastructure.Patients;
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

/// <summary>DossierArchiveChifa : copie des fichiers Chifa lus (GED\Chifa).</summary>
/// <param name="FichierSuperAdmin">Fichier chiffré du compte super admin (vide : C:\ProgramData\PharmaOfficine\super-admin.dat).</param>
/// <param name="FichierCleIntegrite">Fichier chiffré de la clé de scellement des journaux (vide : C:\ProgramData\PharmaOfficine\cle-integrite.dat).</param>
public sealed record InfrastructureOptions(string ChaineConnexion, string DossierExportChifa, string DossierOrdonnances, string? CheminPgDump = null,
    string? DossierArchiveChifa = null, string? FichierSuperAdmin = null, string? FichierCleIntegrite = null);

public static class DependencyInjection
{
    /// <summary>Fuseau de l'officine (Algérie, UTC+1 toute l'année) : celui de la base (migration 083) et des connexions de l'API.</summary>
    public const string Fuseau = "Africa/Algiers";

    /// <summary>
    /// Chaîne de connexion de l'API : Timezone=Africa/Algiers ajouté si elle n'en précise pas (dates SQL calculées dans le fuseau de
    /// l'officine, même si la base n'a pas encore reçu la migration 083) ; un fuseau déjà précisé est gardé.
    /// </summary>
    public static string AvecFuseau(string chaineConnexion)
    {
        var b = new NpgsqlConnectionStringBuilder(chaineConnexion);
        if (string.IsNullOrWhiteSpace(b.Timezone)) b.Timezone = Fuseau;
        return b.ConnectionString;
    }

    /// <summary>Adaptateurs des ports de l'Application : PostgreSQL, sécurité, horloge, Chifa, impression.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, InfrastructureOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ChaineConnexion))
            throw new InvalidOperationException("Chaîne de connexion « Pharmacie » manquante.");

        services.AddSingleton(NpgsqlDataSource.Create(AvecFuseau(options.ChaineConnexion)));
        services.AddDbContext<PharmaDbContext>((sp, o) => o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

        services.AddSingleton<IHorloge, HorlogeSysteme>();
        services.AddSingleton<VerificateurBcrypt>();
        services.AddSingleton<IVerificateurMotDePasse>(sp => sp.GetRequiredService<VerificateurBcrypt>());
        services.AddSingleton<Application.Administration.IHacheurMotDePasse>(sp => sp.GetRequiredService<VerificateurBcrypt>());
        services.AddScoped<Administration.AdministrationSql>();
        services.AddScoped<Application.Administration.ISeuilsAdmin>(sp => sp.GetRequiredService<Administration.AdministrationSql>());
        services.AddScoped<Application.Administration.IUtilisateursAdmin>(sp => sp.GetRequiredService<Administration.AdministrationSql>());
        services.AddScoped<Application.Administration.IPharmacie>(sp => sp.GetRequiredService<Administration.AdministrationSql>());
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IComptesLecture, ComptesLecture>();
        services.AddScoped<ICatalogueLecture, CatalogueLecture>();
        services.AddScoped<ICatalogueEcriture, CatalogueEcritureSql>();
        services.AddScoped<IClassementProduits, ClassementProduitsSql>();
        services.AddScoped<IBlocageProduits, BlocageProduitsSql>();
        services.AddScoped<Pharma.Application.Stock.ISortiesStock, SortiesStockSql>();
        services.AddScoped<Pharma.Application.Stock.IEntreesStock, EntreesStockSql>();
        services.AddScoped<Pharma.Application.Achats.IGrossistes, Achats.GrossistesSql>();
        services.AddScoped<Pharma.Application.Achats.ICatalogueGrossistes, Achats.CatalogueGrossistesSql>();
        // Ruptures et substitution (lot 7, 116)
        services.AddScoped<Pharma.Application.Achats.IRupturesGrossistes, Achats.RupturesGrossistesSql>();
        services.AddScoped<Pharma.Application.Achats.IDemandesPatients, Achats.DemandesPatientsSql>();
        // Formulaire d'accord SMS (2026-11-16, script 123)
        services.AddScoped<Pharma.Application.Achats.IFormulaireAccordSmsDepot, Achats.FormulaireAccordSmsSql>();
        services.AddSingleton<Pharma.Application.Achats.IDocumentsAccordSms, DocumentsPdf>();
        services.AddScoped<Pharma.Application.Catalogue.ISubstitution, Achats.SubstitutionSql>();
        services.AddScoped<IProduitsModification, ProduitsModificationSql>();
        services.AddScoped<Pharma.Application.Essai.IRetoursEssai, Securite.RetoursEssaiSql>();
        services.AddScoped<IEmplacements, EmplacementsSql>();
        services.AddScoped<Pharma.Application.Referentiels.IMotifsReglables, Referentiels.MotifsReglablesSql>();
        services.AddScoped<IInventaires, InventairesSql>();
        services.AddScoped<Pharma.Application.Mobile.IMobile, MobileSql>();
        services.AddScoped<Pharma.Application.Securite.ICodesComptoir, Securite.CodesComptoirSql>();
        services.AddScoped<Pharma.Application.Securite.IEtatConnexions, Securite.EtatConnexionsSql>();
        services.AddScoped<Pharma.Application.Catalogue.INomenclature, Catalogue.NomenclatureSql>();
        services.AddSingleton<Pharma.Application.Catalogue.ILecteurNomenclature, Catalogue.LecteurNomenclatureXlsx>();
        services.AddScoped<Pharma.Application.Administration.ISeuilsStock, Administration.SeuilsStockSql>();
        services.AddScoped<ILotsLecture, LotsLecture>();
        services.AddScoped<IAlternativesLecture, AlternativesLecture>();
        services.AddScoped<IEquivalentsComptoir, EquivalentsComptoirSql>();
        services.AddScoped<IStockLecture, StockLecture>();
        services.AddScoped<IEtiquetageLecture, EtiquetageLecture>();
        services.AddScoped<IAlertesLecture, AlertesLecture>();
        services.AddScoped<Pharma.Application.Alertes.IIndicateursAlertes, Alertes.IndicateursAlertesSql>();
        services.AddScoped<IMoteurAlertes, MoteurAlertesSql>();
        services.AddScoped<Application.Alertes.IAlertesLotsDelaisses, Lecture.AlertesLotsDelaissesSql>();
        services.AddScoped<Application.Surveillance.ISurveillance, Surveillance.SurveillanceSql>();
        services.AddScoped<Application.Surveillance.IRisques, Surveillance.RisquesSql>();
        services.AddScoped<Application.Caisse.IVoletsChifa, Caisse.VoletsChifaSql>();
        services.AddScoped<Application.Caisse.IRapprochementsVolet, Caisse.RapprochementsVoletSql>();
        services.AddScoped<Application.Chifa.IVentesChifa, Chifa.VentesChifaSql>();
        services.AddScoped<Application.Chifa.IConflitsFactureChifa, Chifa.ConflitsFactureChifaSql>();
        services.AddScoped<Application.Chifa.ICentresPayeurs, Chifa.CentresPayeursSql>();
        services.AddScoped<Application.Chifa.IBordereaux, Chifa.BordereauxSql>();
        services.AddScoped<Application.Caisse.ITypesPrestation, Caisse.TypesPrestationSql>();
        services.AddScoped<Application.Catalogue.IIndications, Lecture.IndicationsSql>();
        services.AddSingleton<Application.Commun.ITraceErreurs, Systeme.TraceErreurs>();
        services.AddScoped<IAlertesEcriture, AlertesEcriture>();
        services.AddScoped<IRappelLot, RappelLotSql>();
        // Sérialisation (script 120) : adaptateur FACTICE tant que l'API du MIPH n'est pas connue [À VÉRIFIER]
        services.AddScoped<Application.Serialisation.IFileSerialisation, Serialisation.FileSerialisationSql>();
        services.AddScoped<Application.Serialisation.ISerialisationConnecteur, Serialisation.SerialisationFactice>();
        services.AddScoped<JournalSql>();
        services.AddScoped<IJournal>(sp => sp.GetRequiredService<JournalSql>());
        services.AddScoped<IJournalLecture>(sp => sp.GetRequiredService<JournalSql>());
        services.AddScoped<PatientsSql>();
        services.AddScoped<IPatients>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<IMedecins>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<IOrganismes>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<ISuiviDroitsChifa, SuiviDroitsChifaSql>();
        services.AddScoped<IPreparationVente, PreparationVenteSql>();
        services.AddScoped<IVentesEcriture, VentesEcritureSql>();
        services.AddScoped<RemisesAutomatiquesSql>();
        services.AddScoped<IRemisesAutomatiques>(sp => sp.GetRequiredService<RemisesAutomatiquesSql>());
        services.AddScoped<IRemisesAutomatiquesAdmin>(sp => sp.GetRequiredService<RemisesAutomatiquesSql>());
        services.AddScoped<ISessionsCaisse, SessionsCaisseSql>();
        services.AddScoped<IEcartsCaisseRecents, EcartsCaisseRecentsSql>();
        services.AddSingleton<IDocumentsPdf, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Rapports.IDocumentsRapports, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Stock.IDocumentsRegistre, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Factures.IDocumentsFactures, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Stock.IDocumentsInventaire, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Caisse.IDocumentsCredit, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Caisse.IDocumentsTicket, DocumentsPdf>();
        // Sorties et entrées d'argent par le tiroir (script 096) : écritures et bon de sortie
        services.AddScoped<Pharma.Application.Caisse.ITiroir, TiroirSql>();
        services.AddSingleton<Pharma.Application.Caisse.IDocumentsBonSortie, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Chifa.IDocumentsBordereau, DocumentsPdf>();
        services.AddScoped<Pharma.Application.Stock.ICampagnesInventaire, Lecture.CampagnesInventaireSql>();
        services.AddScoped<Pharma.Application.Pilotage.IPilotage, Lecture.PilotageSql>();
        // Tableau de bord financier (lot 11)
        services.AddScoped<Pharma.Application.Pilotage.IPilotageFinancier, Lecture.PilotageFinancierSql>();
        services.AddSingleton<Pharma.Application.Pilotage.IDocumentsPilotageFinancier, DocumentsPdf>();
        services.AddScoped<Pharma.Application.Stock.IRegistrePsychotropes, RegistreSql>();
        services.AddScoped<Pharma.Application.Caisse.ITicketLecture, Caisse.TicketSql>();
        services.AddScoped<Pharma.Application.Caisse.IRecusCredit, Caisse.RecusCreditSql>();
        services.AddScoped<Pharma.Application.Securite.IPreferencesAffichage, Securite.PreferencesAffichageSql>();
        services.AddScoped<Pharma.Application.Caisse.IVentesEnAttente, Caisse.VentesEnAttenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IEtatIntermediaireCaisse, Caisse.EtatIntermediaireSql>();
        services.AddScoped<Pharma.Application.Achats.IReceptionsEnAttente, Achats.ReceptionsEnAttenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IAnnulationsVente, Caisse.AnnulationsVenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IVentesParBoite, Caisse.AnnulationsVenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IRetoursClient, Caisse.RetoursClientSql>();
        services.AddScoped<Pharma.Application.Caisse.ICreditsClients, Caisse.CreditsClientsSql>();
        services.AddScoped<Pharma.Application.Caisse.IAvoirs, Caisse.AvoirsSql>();
        services.AddScoped<Pharma.Application.Suivi.ISuiviDettes, Suivi.SuiviDettesSql>();
        services.AddScoped<Pharma.Application.Contacts.IContacts, Contacts.ContactsSql>();
        services.AddScoped<Pharma.Application.Securite.IDetenteursDroit, Securite.DetenteursDroitSql>();
        services.AddScoped<Pharma.Application.Tresorerie.ITresorerie, Tresorerie.TresorerieSql>();
        services.AddScoped<Pharma.Application.Factures.IFactures, Factures.FacturesSql>();
        services.AddScoped<Pharma.Application.Achats.IReglementsFournisseurs, Achats.ReglementsFournisseursSql>();
        services.AddScoped<Pharma.Application.Caisse.IAnnulationsReglementTiroir, Achats.ReglementsFournisseursSql>();
        services.AddScoped<Pharma.Application.Achats.ITraites, Achats.TraitesSql>();
        services.AddScoped<Pharma.Application.Financements.IFinancements, Financements.FinancementsSql>();
        services.AddSingleton<Pharma.Application.Financements.ILecteurTableau, Financements.LecteurTableau>();
        services.AddScoped<Pharma.Application.Rapports.IRapports, RapportsSql>();
        services.AddSingleton<Pharma.Application.Reprise.ISourceReprise, Reprise.SourceOdbc>();
        services.AddScoped<Pharma.Application.Reprise.IRepriseImport, Reprise.RepriseSql>();
        services.AddScoped<Pharma.Application.Reprise.IDoublonsImport, Reprise.DoublonsImportSql>();
        services.AddScoped<Pharma.Application.Reprise.IProfilsReprise, Reprise.ProfilsRepriseSql>();
        services.AddScoped<Pharma.Application.Reprise.IHistoriqueChifa, Reprise.HistoriqueChifaSql>();
        services.AddScoped<Application.Ordonnances.IRapprochementCatalogue, Ordonnances.RapprochementSql>();
        services.AddSingleton<Application.Ordonnances.IStockageOrdonnances>(new Ordonnances.StockageOrdonnancesFichiers(options.DossierOrdonnances));
        services.AddScoped<ITracabiliteLecture, TracabiliteLecture>();
        services.AddSingleton<CodesInternesLot>();
        services.AddScoped<Application.Achats.IAchats, Achats.AchatsSql>();
        services.AddScoped<Application.Achats.IComptagesReception, Achats.ComptagesReceptionSql>();
        services.AddSingleton<Application.Achats.IDocumentsAchats, DocumentsAchats>();
        services.AddScoped<Application.Notifications.INotificationsDepot, Notifications.NotificationsSql>();
        services.AddScoped<Application.Notifications.ICodesVerificationDestinataire, Notifications.CodesVerificationDestinataireSql>();
        services.AddScoped<Application.Notifications.IFileMessagesLecture, Notifications.NotificationsSql>();
        services.AddScoped<Application.Notifications.IDonneesRapport, Notifications.DonneesRapportSql>();
        services.AddScoped<Application.Notifications.IAlertesNonEnvoyees, Notifications.AlertesNonEnvoyeesSql>();
        services.AddSingleton<Application.Notifications.IProtectionSecret, Notifications.ProtectionDpapi>();
        services.AddSingleton<Application.Notifications.IEnvoiEmail, Notifications.EnvoiEmailMailKit>();
        services.AddSingleton<Application.Achats.IEnvoiEmailPieceJointe, Notifications.EnvoiEmailMailKit>();
        services.AddSingleton<Application.Notifications.IEnvoiSms, Notifications.EnvoiSmsModemGsm>();
        services.AddSingleton<Application.SuperAdmin.ICompteSuperAdmin>(new Securite.CompteSuperAdminFichier(
            string.IsNullOrWhiteSpace(options.FichierSuperAdmin)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "super-admin.dat")
                : options.FichierSuperAdmin));
        services.AddScoped<Application.SuperAdmin.ITitulaireLecture, Securite.TitulaireLecture>();
        // Intégrité des journaux : clé de scellement hors de la base (DPAPI machine), registre et vérifications en base (script 111)
        var scelleur = new Securite.ScelleurIntegriteLocal(
            string.IsNullOrWhiteSpace(options.FichierCleIntegrite)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "cle-integrite.dat")
                : options.FichierCleIntegrite);
        services.AddSingleton<Application.Integrite.IScelleurIntegrite>(scelleur);
        // 2e serveur (script 122) : export / import de la clé (même fichier), réglages par serveur, réplication
        services.AddSingleton<Application.SecondServeur.ICleIntegriteStockee>(scelleur);
        services.AddScoped<Application.SecondServeur.ISecondServeurDepot, SecondServeur.SecondServeurSql>();
        services.AddScoped<Application.Integrite.IIntegriteDepot, Securite.IntegriteSql>();
        // Registre des psychotropes renforcé (lot 8, script 117) ; répertoire national des ordonnances : adaptateur FACTICE
        services.AddScoped<Application.Psychotropes.IRegistreRenforceDepot, Psychotropes.RegistreRenforceSql>();
        services.AddScoped<Application.Psychotropes.IZonePsychotropesDepot, Psychotropes.RegistreRenforceSql>();
        services.AddSingleton<Application.Psychotropes.IRepertoireOrdonnancesConnecteur, Psychotropes.RepertoireOrdonnancesFactice>();
        // Journal fiscal des ventes (lot 3, script 112) : lignes et clôtures en base, rapports scellés archivés sur le PC serveur
        services.AddScoped<Application.Fiscal.IJournalFiscalDepot, Fiscal.JournalFiscalSql>();
        services.AddSingleton<Application.Fiscal.IDocumentsFiscaux, DocumentsPdf>();
        services.AddSingleton<Application.Fiscal.IArchivesFiscales>(new Fiscal.ArchivesFiscalesLocales(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "archives-fiscales")));
        // Restauration : scripts database/migration/0xx livrés avec l'API (sous-dossier « database »)
        services.AddScoped<Application.SuperAdmin.IRestaurationBase>(sp => new Sauvegardes.RestaurationPostgres(
            options.ChaineConnexion, options.CheminPgDump, Path.Combine(AppContext.BaseDirectory, "database"), sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddScoped<Application.Sauvegardes.ISauvegardesDepot, Sauvegardes.SauvegardesSql>();
        // PC de secours : réglages et état de la seconde copie hors de la base (C:\ProgramData\PharmaOfficine\copie-secours.json)
        services.AddSingleton<Application.Sauvegardes.ICopieSecoursDepot>(new Sauvegardes.CopieSecoursFichier(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "copie-secours.json")));
        services.AddSingleton<Application.Sauvegardes.IFichiersCopieSecours, Sauvegardes.FichiersCopieSecours>();
        services.AddScoped<Application.Sauvegardes.ITestRestauration>(sp => new Sauvegardes.RestaurationPostgres(
            options.ChaineConnexion, options.CheminPgDump, Path.Combine(AppContext.BaseDirectory, "database"), sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<Application.Ged.IGed, Ged.GedFichiers>();
        services.AddSingleton<Application.Sauvegardes.IStockageSauvegarde>(new Sauvegardes.StockagePgDump(options.ChaineConnexion, options.CheminPgDump));
        services.AddSingleton<IChifaConnecteur>(sp => new ChifaFichiersConnecteur(
            options.DossierExportChifa,
            sp.GetRequiredService<ILogger<ChifaFichiersConnecteur>>(), options.DossierArchiveChifa));

        return services;
    }
}
