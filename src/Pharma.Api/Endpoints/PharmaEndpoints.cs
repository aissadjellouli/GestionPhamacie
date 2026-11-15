using Pharma.Api.Securite;

namespace Pharma.Api.Endpoints;

public static class PharmaEndpoints
{
    public static void MapPharmaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/sante", () => Results.Ok(new { statut = "ok", heure = DateTime.Now }));

        // Toutes les routes : refus prévus (règle métier, droit) renvoyés au poste sans être traités comme des pannes
        var racine = app.MapGroup("").GererErreursMetier();
        racine.MapAuth();
        racine.MapSuperAdmin();

        var api = racine.MapGroup("/api");
        api.MapCatalogue();
        api.MapAlertes();
        api.MapStock();
        api.MapCaisse();
        api.MapPatients();
        api.MapOrdonnances();
        api.MapAdmin();
        api.MapTracabilite();
        api.MapAchats();
        api.MapNotifications();
        api.MapEmplacements();
        api.MapMotifsReglables();
        api.MapInventaire();
        api.MapMobile();
        api.MapRapports();
        api.MapReprise();
        api.MapSurveillance();
        api.MapNomenclature();
        api.MapRegistre();
        api.MapReglements();
        api.MapTresorerie();
        api.MapFinancements();
        api.MapFactures();
        api.MapInventaireComplet();
        api.MapPilotage();
        api.MapSuiviDettes();
        api.MapContacts();
        api.MapRetoursEssai();
        api.MapEtatPoste();
        api.MapGed();
    }
}
