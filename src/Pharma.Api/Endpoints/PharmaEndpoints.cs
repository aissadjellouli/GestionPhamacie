using Pharma.Api.Securite;

namespace Pharma.Api.Endpoints;

public static class PharmaEndpoints
{
    public static void MapPharmaEndpoints(this WebApplication app)
    {
        // Santé du serveur (2026-11-19 : /api/healthcheck, ancien nom /api/sante retiré sur demande du titulaire)
        // Serveur écarté (lot 3) : statut reste « ok » (le serveur répond) ; ecarte = vrai et serveurRetenu = adresse du serveur retenu (null si indécis) le signalent
        // Vague 2 : role = principal | veille | ecarte (ajout seul ; ecarte l'emporte, puis veille)
        app.MapGet("/api/healthcheck", (Pharma.Application.SecondServeur.IServeurEcarteDetecte ecart, Pharma.Application.SecondServeur.IVeilleServeur veille) =>
            Results.Ok(new
            {
                statut = "ok",
                heure = DateTime.Now,
                ecarte = ecart.Ecart is not null,
                serveurRetenu = ecart.Ecart?.ServeurRetenu,
                role = BarrageServeurVeille.Role(ecart.Ecart is not null, veille.EstVeille)
            }));
        // Vague 2, lot V2-2 : état signé de ce serveur pour l'autre serveur (signature par l'intergiciel InterServeursMiddleware)
        app.MapInterServeurs();
        // Mode démonstration (docs/demo-cloud.md, étape 2) : lu sans connexion par le poste et la page mobile (bandeau permanent)
        app.MapGet("/api/demo", (Microsoft.Extensions.Options.IOptions<Pharma.Api.Demo.ReglagesDemo> demo) =>
            Results.Ok(new Pharma.Shared.EtatDemoDto(demo.Value.Actif, demo.Value.Actif ? Pharma.Application.Demo.ModeDemo.Libelle : null)));

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
        api.MapSuggestions();
        api.MapOrdonnances();
        api.MapAdmin();
        api.MapPostes();
        api.MapTracabilite();
        api.MapSerialisation();
        api.MapAchats();
        api.MapMultiGrossistes();
        api.MapRuptures();
        api.MapNotifications();
        api.MapEmplacements();
        api.MapMotifsReglables();
        api.MapChifaComplements();
        api.MapPrestations();
        api.MapIndications();
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
        api.MapPilotageFinancier();
        api.MapSuiviDettes();
        api.MapContacts();
        api.MapRetoursEssai();
        api.MapEtatPoste();
        api.MapGed();
        api.MapVigie();
        api.MapSecondServeur();
        api.MapSynchronisation();
        api.MapReglagesMutualises();
    }
}
