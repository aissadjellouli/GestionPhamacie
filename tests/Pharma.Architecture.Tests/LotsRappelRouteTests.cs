namespace Pharma.Architecture.Tests;

/// <summary>
/// Numéros de lot proposés au rappel (décision du titulaire, 2026-11-18) : route de la traçabilité, même droit que le rappel de lot
/// (stock.declarer_perime), et le poste l'utilise (les lots épuisés sont proposés, « épuisé » dans le détail).
/// </summary>
public class LotsRappelRouteTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", .. chemin]));

    private static string Route(string source, string route)
    {
        var debut = source.IndexOf(route, StringComparison.Ordinal);
        Assert.True(debut > 0, $"route {route} introuvable");
        var suite = source[debut..];
        var fin = suite.IndexOf("api.Map", StringComparison.Ordinal);
        return fin > 0 ? suite[..fin] : suite;
    }

    [Fact]
    public void La_route_des_lots_du_rappel_exige_le_droit_du_rappel()
    {
        var api = Lire("Pharma.Api", "Endpoints", "TracabiliteEndpoints.cs");
        Assert.Contains(".ExigerPermission(Permissions.StockDeclarerPerime)", Route(api, "api.MapPost(\"/tracabilite/rappels\""));
        var lots = Route(api, "\"/tracabilite/produits/{id:long}/lots\"");
        Assert.Contains("api.MapGet(", api[..api.IndexOf("\"/tracabilite/produits/{id:long}/lots\"", StringComparison.Ordinal)][^20..]);
        Assert.Contains("ProposerLotsRappel", lots);
        Assert.Contains(".ExigerPermission(Permissions.StockDeclarerPerime)", lots);
    }

    [Fact]
    public void Le_poste_propose_les_lots_epuises_par_cette_route()
    {
        var client = Lire("Pharma.Desktop", "Services", "ApiClient.cs");
        Assert.Contains("api/tracabilite/produits/{produitId}/lots?q={Uri.EscapeDataString(q)}", client);
        var ecran = Lire("Pharma.Desktop", "Composants", "Pages", "Tracabilite.razor");
        Assert.Contains("Api.LotsRappelAsync(", ecran);
        Assert.DoesNotContain("Api.LotsEnStockAsync(", ecran);
        Assert.Contains("\"épuisé\"", ecran);
    }
}
