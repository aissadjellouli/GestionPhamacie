using System.Text.RegularExpressions;
using Pharma.Api.Demo;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot S7.2b : Administration › Synchronisation › Accès à distance. Rubrique du menu, explication sans bouton si la synchronisation
/// n'est pas activée, mot de passe ressaisi, code montré en grand (classe de la charte, clair et sombre), jamais gardé, aucun style en
/// ligne ; route locale réservée (titulaire + mot de passe dans le cas d'usage), coupée en démonstration.
/// </summary>
public class AccesDistantEcranTests
{
    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([BaseJetableM4.RacineDepot(), "src", .. chemin]));

    private static string Page() => Lire("Pharma.Desktop", "Composants", "Pages", "AdminSynchronisation.razor");

    /// <summary>Bloc @if (… rubrique == "acces-distant") { … } de la page.</summary>
    private static string Bloc()
    {
        var page = Page();
        var m = Regex.Match(page, @"\n(?<retrait> *)@if \(t is not null && rubrique == ""acces-distant""\)\s*\n\k<retrait>\{(?<corps>.*?)\n\k<retrait>\}", RegexOptions.Singleline);
        Assert.True(m.Success, "rubrique « acces-distant » introuvable");
        return m.Groups["corps"].Value;
    }

    [Fact]
    public void Rubrique_du_menu_de_la_synchronisation()
    {
        var nav = Lire("Pharma.Desktop", "Composants", "Mise", "NavAdmin.razor");
        Assert.Contains("(\"acces-distant\", \"Accès à distance\")", nav);
    }

    [Fact]
    public void Sans_synchronisation_activee_explication_et_aucun_bouton()
    {
        var bloc = Bloc();
        Assert.Contains("private bool AccesDistantPossible => t is { Active: true, Activee: true, BaseEnLectureSeule: false };", Page());
        var sans = bloc[bloc.IndexOf("@if (!AccesDistantPossible)", StringComparison.Ordinal)..];
        sans = sans[..sans.IndexOf("else if (!EstTitulaire)", StringComparison.Ordinal)];
        Assert.Contains("La synchronisation n’est pas activée", sans);
        Assert.DoesNotContain("<button", sans);
        Assert.DoesNotContain("<input", sans);
        // Seul le titulaire voit le formulaire
        var nonTitulaire = bloc[bloc.IndexOf("else if (!EstTitulaire)", StringComparison.Ordinal)..];
        nonTitulaire = nonTitulaire[..nonTitulaire.IndexOf("else if (codeAcces", StringComparison.Ordinal)];
        Assert.DoesNotContain("<button", nonTitulaire);
    }

    [Fact]
    public void Formulaire_mot_de_passe_ressaisi_code_en_grand_jamais_garde_sans_style_en_ligne()
    {
        var bloc = Bloc();
        var page = Page();
        Assert.DoesNotContain("style=", bloc);
        Assert.Contains("type=\"password\" autocomplete=\"current-password\"", bloc);
        Assert.Contains("data-champ=\"compte\"", bloc);
        Assert.Contains("data-champ=\"mot_de_passe\"", bloc);
        Assert.Contains(">Générer le code</button>", bloc);
        Assert.Contains("class=\"code-acces-distant\"", bloc);
        Assert.Contains("dans les 30 minutes", bloc);
        Assert.Contains("/rattacher", bloc);
        // Mot de passe effacé après l'essai ; code et mot de passe effacés au changement de rubrique ; rien dans le navigateur
        Assert.Contains("motDePasseAcces = \"\";   // jamais gardé", page);
        Assert.Contains("if (rubrique != \"acces-distant\") (codeAcces, motDePasseAcces) = (null, \"\");", page);
        Assert.DoesNotContain("localStorage", page);
        Assert.DoesNotContain("sessionStorage", page);
        Assert.Contains("\"api/admin/synchronisation/acces-distant/code\"", Lire("Pharma.Desktop", "Services", "ApiClient.cs"));
    }

    [Fact]
    public void Classe_de_la_charte_couleurs_par_variables_clair_et_sombre()
    {
        var css = Lire("Pharma.Desktop", "wwwroot", "css", "app.css");
        var i = css.IndexOf(".code-acces-distant {", StringComparison.Ordinal);
        Assert.True(i > 0);
        var regle = css[i..css.IndexOf('}', i)];
        Assert.Contains("var(--surface-2)", regle);
        Assert.Contains("var(--accent-texte)", regle);
        Assert.Contains("var(--bordure)", regle);
        Assert.DoesNotMatch("#[0-9A-Fa-f]{3,6}", regle);   // aucune couleur fixe : le mode sombre redéfinit les variables
    }

    [Fact]
    public void Route_locale_titulaire_et_mot_de_passe_et_coupee_en_demonstration()
    {
        var points = Lire("Pharma.Api", "Endpoints", "SynchronisationEndpoints.cs");
        var groupe = points.IndexOf("var s = api.MapGroup(\"/admin/synchronisation\").ExigerPermission(Permissions.AdminParametres);", StringComparison.Ordinal);
        var route = points.IndexOf("s.MapPost(\"/acces-distant/code\"", StringComparison.Ordinal);
        Assert.True(groupe > 0 && route > groupe, "route sous le groupe d'administration de la synchronisation");
        var bloc = points[route..points.IndexOf("});", route, StringComparison.Ordinal)];
        Assert.Contains("generer.ExecuterAsync(d.Compte, d.MotDePasse, user.UtilisateurId(), ct)", bloc);
        Assert.Contains("CacheControl = \"no-store\"", bloc);
        var cas = Lire("Pharma.Application", "Synchronisation", "AccesDistant.cs");
        var executer = cas[cas.IndexOf("public async Task<CodeRattachementSync> ExecuterAsync(", StringComparison.Ordinal)..];
        var titulaire = executer.IndexOf("await droits.ExigerTitulaireAsync(", StringComparison.Ordinal);
        var mdp = executer.IndexOf("await motDePasse.VerifierAsync(", StringComparison.Ordinal);
        var appel = executer.IndexOf("client.CodeRattachementAsync(", StringComparison.Ordinal);
        Assert.True(titulaire >= 0 && titulaire < mdp && mdp < appel, "titulaire, puis mot de passe, puis serveur central");
        Assert.NotNull(FonctionsCoupeesDemo.FonctionCoupee("POST", "/api/admin/synchronisation/acces-distant/code"));
    }
}
