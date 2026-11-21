using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pharma.Cloud.Web.Composants.Partages;
using Pharma.Cloud.Web.Donnees;
using Pharma.Cloud.Web.Securite;

namespace Pharma.Cloud.Web.Tests;

/// <summary>Tuiles navigables du central : chaque tuile est un lien vers une route réelle permise à son rôle, jamais à l'autre rôle.</summary>
public class TuilesEcransTests
{
    private static readonly Guid Officine = Guid.Parse("5b0c2f4e-1d2a-4c3b-9e8f-0a1b2c3d4e5f");
    private static SessionEcran S(string role) => new(1, Guid.NewGuid(), "a.b", "A", role, "ouverte", DateTime.UtcNow.AddHours(1), false);
    private static string Pages => Path.Combine(Depot.Racine(), "src", "Pharma.Cloud.Web", "Composants", "Pages");

    private static readonly string[][] JeuxDeDroits = [[], ["consulter"], ["consulter", "journal_acces"], ["consulter", "ordres"], ["consulter", "journal_acces", "ordres"]];
    private static readonly string?[] Causes = [null, "restauration", "nouvelle_epoque", "non_expliquee", "cause_inconnue"];

    private static IEnumerable<LienTuile> LiensTitulaire() =>
        from droits in JeuxDeDroits from t in LiensTuiles.TuilesTitulaire select LiensTuiles.Titulaire(t, Officine, droits);

    private static IEnumerable<LienTuile> LiensEditeur() =>
        from cause in Causes from page in new[] { 1, 3 } from t in LiensTuiles.TuilesEditeur select LiensTuiles.Editeur(t, Officine, cause, page);

    /// <summary>Chemin servi (adresse relative à la balise base « / », sans requête ni ancre).</summary>
    private static string Chemin(LienTuile l) => "/" + l.Adresse.Split('?', '#')[0];

    /// <summary>Routes réelles : les @page des écrans, {…:guid} remplacé par un uuid.</summary>
    private static IReadOnlyList<Regex> Routes() =>
        Directory.GetFiles(Pages, "*.razor").SelectMany(f => Regex.Matches(File.ReadAllText(f), @"@page ""([^""]+)""").Select(m => m.Groups[1].Value))
            .Select(r => new Regex("^" + Regex.Replace(Regex.Escape(r), @"\\\{\w+:guid}", "[0-9a-f-]{36}") + "$", RegexOptions.IgnoreCase)).ToList();

    [Fact]
    public void Tuiles_du_titulaire_vers_des_routes_reelles_permises_au_titulaire_jamais_a_l_editeur()
    {
        var routes = Routes();
        foreach (var l in LiensTitulaire())
        {
            Assert.Contains(routes, r => r.IsMatch(Chemin(l)));
            Assert.Null(ControleAcces.Redirection(Chemin(l), S("titulaire")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(Chemin(l), S("editeur")));
            Assert.StartsWith("mes-officines", l.Adresse);
            Assert.False(string.IsNullOrWhiteSpace(l.Infobulle));
        }
    }

    [Fact]
    public void Tuiles_de_l_editeur_vers_des_routes_reelles_permises_a_l_editeur_jamais_au_titulaire()
    {
        var routes = Routes();
        var rubriques = Rubriques.Toutes.Select(r => r.Cle).ToHashSet();
        foreach (var l in LiensEditeur())
        {
            Assert.Contains(routes, r => r.IsMatch(Chemin(l)));
            Assert.Null(ControleAcces.Redirection(Chemin(l), S("editeur")));
            Assert.Equal("/acces-refuse", ControleAcces.Redirection(Chemin(l), S("titulaire")));
            Assert.Contains(Regex.Match(l.Adresse, @"rubrique=(\w+)").Groups[1].Value, rubriques);
            Assert.False(string.IsNullOrWhiteSpace(l.Infobulle));
        }
    }

    [Fact]
    public void Ordres_seulement_avec_le_droit_ordres_sinon_vue_d_ensemble()
    {
        Assert.Equal($"mes-officines/{Officine}/ordres", LiensTuiles.Titulaire("ordres_en_attente", Officine, ["consulter", "ordres"]).Adresse);
        Assert.Equal("mes-officines/ensemble", LiensTuiles.Titulaire("ordres_en_attente", Officine, ["consulter", "journal_acces"]).Adresse);
        // Aucune autre tuile ne mène à un écran soumis à un droit (journal des accès, ordres)
        foreach (var t in LiensTuiles.TuilesTitulaire.Where(t => t != "ordres_en_attente"))
            Assert.DoesNotMatch(@"/(journal|ordres)$", LiensTuiles.Titulaire(t, Officine, ["consulter", "journal_acces", "ordres"]).Adresse);
    }

    [Theory]
    [InlineData("restauration", "rubrique=restaurations")]
    [InlineData("nouvelle_epoque", "rubrique=changements")]
    [InlineData("non_expliquee", "rubrique=absentes")]
    [InlineData(null, "rubrique=absentes")]
    public void Cause_probable_vers_la_rubrique_qui_l_explique(string? cause, string attendu) =>
        Assert.Contains(attendu, LiensTuiles.Editeur("cause", Officine, cause, 1).Adresse);

    [Fact]
    public void Total_et_tables_vers_l_ancre_du_tableau_en_gardant_la_page()
    {
        Assert.Equal($"officines/{Officine}?rubrique=absentes#{LiensTuiles.AncreAbsentes}", LiensTuiles.Editeur("total", Officine, null, 1).Adresse);
        Assert.Equal($"officines/{Officine}?rubrique=absentes&page=3#{LiensTuiles.AncreAbsentes}", LiensTuiles.Editeur("tables", Officine, null, 3).Adresse);
        Assert.Equal($"officines/{Officine}?rubrique=chargements", LiensTuiles.Editeur("dernier_chargement", Officine, null, 3).Adresse);
    }

    [Fact]
    public void Toutes_les_tuiles_des_ecrans_ont_un_lien_et_chaque_tuile_connue_est_rendue()
    {
        var tableau = File.ReadAllText(Path.Combine(Pages, "TableauBord.razor"));
        var officine = File.ReadAllText(Path.Combine(Pages, "Officine.razor"));
        // Aucune tuile écrite à la main (div class="tuile") dans les écrans : toutes passent par TuileTableau
        foreach (var f in Directory.GetFiles(Pages, "*.razor"))
            Assert.DoesNotMatch(@"class=""tuile[ ""]", File.ReadAllText(f));
        foreach (var f in new[] { tableau, officine })
            foreach (Match m in Regex.Matches(f, @"<TuileTableau\b[^>]*"))
                Assert.Contains("Lien=", m.Value);
        Assert.Equal(LiensTuiles.TuilesTitulaire, Regex.Matches(tableau, @"Lien=""@Lien\(""(\w+)""\)""").Select(m => m.Groups[1].Value));
        Assert.Equal(LiensTuiles.TuilesEditeur, Regex.Matches(officine, @"Lien=""@LienAbsentes\(""(\w+)""\)""").Select(m => m.Groups[1].Value));
        Assert.Contains("Id=\"@LiensTuiles.AncreAbsentes\"", officine);
    }

    [Fact]
    public async Task Tuile_rendue_en_lien_avec_infobulle_ou_en_bloc_sans_lien()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        await using var rendu = new HtmlRenderer(services, NullLoggerFactory.Instance);
        async Task<string> Rendre(LienTuile? lien) => await rendu.Dispatcher.InvokeAsync(async () =>
            (await rendu.RenderComponentAsync<TuileTableau>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(TuileTableau.Libelle)] = "Ventes du jour", [nameof(TuileTableau.Valeur)] = "12", [nameof(TuileTableau.Note)] = "tickets",
                [nameof(TuileTableau.Lien)] = lien,
            }))).ToHtmlString());

        var lien = await Rendre(LiensTuiles.Titulaire("ventes_jour", Officine, ["consulter"]));
        Assert.StartsWith("<a class=\"tuile tuile-lien\" href=\"mes-officines/ensemble\" title=\"Ouvre la vue d", lien);
        Assert.Contains("<span class=\"tuile-valeur\">12</span>", lien);
        Assert.Contains("<span class=\"tuile-note\">tickets</span>", lien);
        var bloc = await Rendre(null);
        Assert.StartsWith("<div class=\"tuile\">", bloc);
        Assert.DoesNotContain("href", bloc);
    }
}
