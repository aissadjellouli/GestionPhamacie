namespace Pharma.Architecture.Tests;

/// <summary>
/// Patients en attente avec le seul droit de vendre (2026-11-20, docs/navigation.md) : le choix du produit passe par la recherche du
/// comptoir sans stock.consulter (GET /api/produits reste réservé au stock), les routes de l'écran exigent caisse.vendre, et la
/// colonne de la Caisse ouvre l'écran.
/// </summary>
public class PatientsAttenteCaisseTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RacineDepot(), "src", .. chemin]));

    [Fact]
    public void ChoixProduit_passe_par_la_recherche_du_comptoir_sans_le_droit_de_consulter_le_stock()
    {
        var choix = Lire("Pharma.Desktop", "Composants", "Mise", "ChoixProduit.razor");
        Assert.Contains("@inject SessionPoste Session", choix);
        Assert.Contains("FiltreProduit.Source(Session.A)", choix);
        // stock.consulter : catalogue ; caisse.vendre : recherche du comptoir (produit gardé) ; sinon rien
        var source = choix[choix.IndexOf("FiltreProduit.Source(Session.A)", StringComparison.Ordinal)..];
        var produits = source.IndexOf("case \"produits\":", StringComparison.Ordinal);
        var caisse = source.IndexOf("case \"caisse\":", StringComparison.Ordinal);
        var aucun = source.IndexOf("default:", StringComparison.Ordinal);
        Assert.True(produits > 0 && caisse > produits && aucun > caisse, "Ordre : catalogue, comptoir, aucun produit");
        Assert.Contains("Api.RechercherProduitsAsync(q)", source[produits..caisse]);
        Assert.Contains("Api.RechercherAuComptoirAsync(q)", source[caisse..aucun]);
        Assert.Contains("comptoir.Select(c => c.Produit)", source[caisse..aucun]);
        Assert.Contains("return [];", source[aucun..]);
        // Produits désactivés : jamais de bascule vers le comptoir
        Assert.Contains("if (InclureInactifs) return (await Api.RechercherProduitsAsync(q, true)).Item1;", choix);
        // La recherche n'appelle plus directement le catalogue
        Assert.DoesNotContain("Api.RechercherProduitsAsync(texte.Trim(), InclureInactifs)", choix);
    }

    [Fact]
    public void La_regle_de_source_reste_celle_du_filtre_produit()
    {
        var filtre = Lire("Pharma.Desktop", "Composants", "Mise", "FiltreProduit.razor");
        Assert.Contains("a(\"stock.consulter\") ? \"produits\" : a(\"caisse.vendre\") ? \"caisse\" : null", filtre);
    }

    [Fact]
    public void Le_catalogue_reste_reserve_au_stock_et_la_recherche_du_comptoir_ouverte_au_droit_de_vendre()
    {
        var api = Lire("Pharma.Api", "Endpoints", "CatalogueEndpoints.cs");
        Assert.Contains(".ExigerPermission(Permissions.StockConsulter)", Route(api, "api.MapGet(\"/produits\""));
        Assert.Contains("Permissions.CaisseVendre", Route(api, "api.MapGet(\"/caisse/produits\""));
    }

    [Theory]
    [InlineData("d.MapGet(\"\"")]
    [InlineData("d.MapGet(\"/alertes\"")]
    [InlineData("d.MapPost(\"\"")]
    [InlineData("d.MapPost(\"/{id:long}/prevenir\"")]
    [InlineData("d.MapPost(\"/{id:long}/cloturer\"")]
    [InlineData("api.MapGet(\"/patients/{id:long}/consentement-sms\"")]
    [InlineData("api.MapPut(\"/patients/{id:long}/consentement-sms\"")]
    [InlineData("api.MapGet(\"/patients/{id:long}/formulaire-sms.pdf\"")]
    [InlineData("api.MapPut(\"/patients/{id:long}/formulaire-sms\"")]
    public void Les_routes_de_l_ecran_exigent_le_droit_de_vendre(string debut)
    {
        var api = Lire("Pharma.Api", "Endpoints", "RupturesEndpoints.cs");
        Assert.Contains(".ExigerPermission(Permissions.CaisseVendre)", Route(api, debut));
    }

    [Fact]
    public void La_colonne_de_la_caisse_ouvre_les_patients_en_attente_avec_le_droit_de_vendre()
    {
        var nav = Lire("Pharma.Desktop", "Composants", "Caisse", "NavCaisse.razor");
        var vente = nav.IndexOf("@Lien(\"caisse\", \"Vente\")", StringComparison.Ordinal);
        var lien = nav.IndexOf("@Lien(\"patients/attente\", \"Patients en attente\")", StringComparison.Ordinal);
        var operations = nav.IndexOf("Opérations</span>", StringComparison.Ordinal);
        Assert.True(vente > 0 && lien > vente && operations > lien, "Groupe Comptoir : Vente puis Patients en attente");
        Assert.Contains("@if (Session.A(\"caisse.vendre\"))", nav[vente..lien]);
    }

    /// <summary>Texte d'une route : de son début jusqu'à la route suivante (ou la fin du fichier).</summary>
    private static string Route(string source, string debut)
    {
        var i = source.IndexOf(debut, StringComparison.Ordinal);
        Assert.True(i >= 0, $"Route introuvable : {debut}");
        var reste = source[(i + debut.Length)..];
        var suivantes = new[] { "api.Map", "a.Map", "d.Map", "s.Map", "caisse.Map" }
            .Select(m => reste.IndexOf(m, StringComparison.Ordinal)).Where(x => x >= 0).ToList();
        return suivantes.Count == 0 ? reste : reste[..suivantes.Min()];
    }

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
