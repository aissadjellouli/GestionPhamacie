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

public sealed record InfrastructureOptions(string ChaineConnexion, string DossierExportChifa, string DossierOrdonnances, string? CheminPgDump = null);

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
        services.AddScoped<IEmplacements, EmplacementsSql>();
        services.AddScoped<IInventaires, InventairesSql>();
        services.AddScoped<Pharma.Application.Mobile.IMobile, MobileSql>();
        services.AddScoped<Pharma.Application.Securite.ICodesComptoir, Securite.CodesComptoirSql>();
        services.AddScoped<Pharma.Application.Catalogue.INomenclature, Catalogue.NomenclatureSql>();
        services.AddSingleton<Pharma.Application.Catalogue.ILecteurNomenclature, Catalogue.LecteurNomenclatureXlsx>();
        services.AddScoped<Pharma.Application.Administration.ISeuilsStock, Administration.SeuilsStockSql>();
        services.AddScoped<ILotsLecture, LotsLecture>();
        services.AddScoped<IAlternativesLecture, AlternativesLecture>();
        services.AddScoped<IStockLecture, StockLecture>();
        services.AddScoped<IEtiquetageLecture, EtiquetageLecture>();
        services.AddScoped<IAlertesLecture, AlertesLecture>();
        services.AddScoped<IMoteurAlertes, MoteurAlertesSql>();
        services.AddScoped<IAlertesEcriture, AlertesEcriture>();
        services.AddScoped<IRappelLot, RappelLotSql>();
        services.AddScoped<JournalSql>();
        services.AddScoped<IJournal>(sp => sp.GetRequiredService<JournalSql>());
        services.AddScoped<IJournalLecture>(sp => sp.GetRequiredService<JournalSql>());
        services.AddScoped<PatientsSql>();
        services.AddScoped<IPatients>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<IMedecins>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<IOrganismes>(sp => sp.GetRequiredService<PatientsSql>());
        services.AddScoped<IPreparationVente, PreparationVenteSql>();
        services.AddScoped<IVentesEcriture, VentesEcritureSql>();
        services.AddScoped<ISessionsCaisse, SessionsCaisseSql>();
        services.AddSingleton<IDocumentsPdf, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Rapports.IDocumentsRapports, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Stock.IDocumentsRegistre, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Factures.IDocumentsFactures, DocumentsPdf>();
        services.AddSingleton<Pharma.Application.Stock.IDocumentsInventaire, DocumentsPdf>();
        services.AddScoped<Pharma.Application.Stock.ICampagnesInventaire, Lecture.CampagnesInventaireSql>();
        services.AddScoped<Pharma.Application.Pilotage.IPilotage, Lecture.PilotageSql>();
        services.AddScoped<Pharma.Application.Stock.IRegistrePsychotropes, RegistreSql>();
        services.AddScoped<Pharma.Application.Caisse.ITicketLecture, Caisse.TicketSql>();
        services.AddScoped<Pharma.Application.Securite.IPreferencesAffichage, Securite.PreferencesAffichageSql>();
        services.AddScoped<Pharma.Application.Caisse.IVentesEnAttente, Caisse.VentesEnAttenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IAnnulationsVente, Caisse.AnnulationsVenteSql>();
        services.AddScoped<Pharma.Application.Caisse.IRetoursClient, Caisse.RetoursClientSql>();
        services.AddScoped<Pharma.Application.Caisse.ICreditsClients, Caisse.CreditsClientsSql>();
        services.AddScoped<Pharma.Application.Securite.IDetenteursDroit, Securite.DetenteursDroitSql>();
        services.AddScoped<Pharma.Application.Tresorerie.ITresorerie, Tresorerie.TresorerieSql>();
        services.AddScoped<Pharma.Application.Factures.IFactures, Factures.FacturesSql>();
        services.AddScoped<Pharma.Application.Achats.IReglementsFournisseurs, Achats.ReglementsFournisseursSql>();
        services.AddScoped<Pharma.Application.Rapports.IRapports, RapportsSql>();
        services.AddSingleton<Pharma.Application.Reprise.ISourceReprise, Reprise.SourceOdbc>();
        services.AddScoped<Pharma.Application.Reprise.IRepriseImport, Reprise.RepriseSql>();
        services.AddScoped<Application.Ordonnances.IRapprochementCatalogue, Ordonnances.RapprochementSql>();
        services.AddSingleton<Application.Ordonnances.IStockageOrdonnances>(new Ordonnances.StockageOrdonnancesFichiers(options.DossierOrdonnances));
        services.AddScoped<ITracabiliteLecture, TracabiliteLecture>();
        services.AddSingleton<CodesInternesLot>();
        services.AddScoped<Application.Achats.IAchats, Achats.AchatsSql>();
        services.AddSingleton<Application.Achats.IDocumentsAchats, DocumentsAchats>();
        services.AddScoped<Application.Notifications.INotificationsDepot, Notifications.NotificationsSql>();
        services.AddScoped<Application.Notifications.IDonneesRapport, Notifications.DonneesRapportSql>();
        services.AddSingleton<Application.Notifications.IProtectionSecret, Notifications.ProtectionDpapi>();
        services.AddSingleton<Application.Notifications.IEnvoiEmail, Notifications.EnvoiEmailMailKit>();
        services.AddSingleton<Application.Notifications.IEnvoiSms, Notifications.EnvoiSmsModemGsm>();
        services.AddScoped<Application.Sauvegardes.ISauvegardesDepot, Sauvegardes.SauvegardesSql>();
        services.AddSingleton<Application.Sauvegardes.IStockageSauvegarde>(new Sauvegardes.StockagePgDump(options.ChaineConnexion, options.CheminPgDump));
        services.AddSingleton<IChifaConnecteur>(sp => new ChifaFichiersConnecteur(
            options.DossierExportChifa,
            sp.GetRequiredService<ILogger<ChifaFichiersConnecteur>>()));

        return services;
    }
}
