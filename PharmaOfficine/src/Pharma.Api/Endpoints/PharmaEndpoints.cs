namespace Pharma.Api.Endpoints;

public static class PharmaEndpoints
{
    public static void MapPharmaEndpoints(this WebApplication app)
    {
        app.MapGet("/api/sante", () => Results.Ok(new { statut = "ok", heure = DateTime.Now }));
        app.MapAuth();

        var api = app.MapGroup("/api");
        api.MapCatalogue();
        api.MapAlertes();
        api.MapStock();
        api.MapTracabilite();
    }
}
