using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Tout menu dépliable porte son chevron, à tout niveau (décision du titulaire du 2026-11-27) : la liste des écrans « à rubriques »
/// (Domaines.EcransAvecRubriques, qui commande le chevron du menu de gauche) suit les pages qui fournissent des rubriques à NavDomaine.
/// </summary>
public partial class ChevronsMenusTests
{
    // Domaines d'un seul écran (Caisse, Achats) et pages hors domaine (Accueil) : rubriques seules, sans liste d'écrans à replier
    private static readonly HashSet<string> SansListeEcrans = ["caisse", "achats", "accueil"];

    [Fact]
    public void Les_ecrans_a_rubriques_sont_ceux_dont_la_page_donne_des_rubriques_au_menu()
    {
        var racine = BaseJetableM4.RacineDepot();
        var domaines = File.ReadAllText(Path.Combine(racine, "src", "Pharma.Desktop", "Services", "Domaines.cs"));
        var bloc = Regex.Match(domaines, @"EcransAvecRubriques\s*=\s*new HashSet<string>\([^)]*\)\s*\{(?<liste>[^}]*)\}").Groups["liste"].Value;
        var declares = Regex.Matches(bloc, "\"(?<l>[^\"]*)\"").Select(m => m.Groups["l"].Value).ToHashSet();
        Assert.NotEmpty(declares);

        var pages = Path.Combine(racine, "src", "Pharma.Desktop", "Composants", "Pages");
        var trouves = new HashSet<string>();
        foreach (var f in Directory.GetFiles(pages, "*.razor"))
        {
            var t = File.ReadAllText(f);
            var route = Regex.Match(t, "^@page \"/(?<r>[^\"{]*)\"", RegexOptions.Multiline).Groups["r"].Value;
            if (!Regex.IsMatch(t, "^@page ", RegexOptions.Multiline) || SansListeEcrans.Contains(route)) continue;
            // NavDomaine avec contenu (balise non fermante) ou menu du contrôle interne (NavControleInterne fournit les rubriques)
            if (Regex.IsMatch(t, @"<NavDomaine\b[^>]*[^/]>") || t.Contains("<NavControleInterne"))
                trouves.Add(route.StartsWith("surveillance", StringComparison.Ordinal) ? "surveillance" : route);
        }
        var manquants = trouves.Except(declares).ToList();
        var enTrop = declares.Except(trouves).ToList();
        Assert.True(manquants.Count == 0 && enTrop.Count == 0,
            $"Écrans à rubriques sans chevron : {string.Join(", ", manquants)} ; déclarés sans rubriques : {string.Join(", ", enTrop)}");
    }

    /// <summary>
    /// Rubriques repliables homogènes (décision du titulaire du 2026-11-27) : toute rubrique qui a des sous-rubriques est un bouton qui
    /// les replie ou les déplie (GroupeMenu). Plus de titre de groupe statique (.nav-groupe-titre), d'écran déplié non cliquable
    /// (.nav-ecran-ouvert), de chevron « déplié » figé hors de GroupeMenu, ni de thème d'Administration fait à la main.
    /// </summary>
    [Fact]
    public void Aucun_titre_de_groupe_non_cliquable_dans_les_menus()
    {
        var composants = Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var motifs = new[] { "nav-groupe-titre", "nav-ecran-ouvert", "chevron-menu ouvert", "chevron-theme", "class=\"nav-theme" };
        var ecarts = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .Where(f => Path.GetFileName(f) != "GroupeMenu.razor")
            .SelectMany(f => motifs.Where(File.ReadAllText(f).Contains).Select(m => $"{Path.GetRelativePath(composants, f)} ({m})"))
            .ToList();
        Assert.True(ecarts.Count == 0, "Titre de groupe non cliquable (utiliser GroupeMenu) : " + string.Join(", ", ecarts));
    }

    /// <summary>
    /// Pas de titre racine qui enveloppe tout le menu et répète le domaine ou l'écran (décision du titulaire du 2026-11-27 : « la racine
    /// ne sert à rien ») : le menu d'un domaine commence par ses écrans, celui de l'accueil par ses rubriques.
    /// </summary>
    [Fact]
    public void Aucun_titre_racine_au_nom_du_domaine_ou_de_l_accueil()
    {
        var composants = Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var nav = File.ReadAllText(Path.Combine(composants, "Mise", "NavDomaine.razor"));
        Assert.DoesNotContain("Cle=\"@($\"domaine:", nav);
        Assert.DoesNotContain("Titre=\"@domaine.Libelle\"", nav);
        Assert.DoesNotContain("Titre=\"Accueil\"", File.ReadAllText(Path.Combine(composants, "Pages", "Accueil.razor")));
    }

    [Fact]
    public void Le_titre_d_une_rubrique_repliable_est_un_bouton_accessible()
    {
        var groupe = File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", "Composants", "Mise", "GroupeMenu.razor"));
        Assert.Contains("<button type=\"button\"", groupe);
        Assert.Contains("aria-expanded=", groupe);
        Assert.Contains("aria-controls=\"@idContenu\"", groupe);
        Assert.Contains("hidden=\"@(!ouvert)\"", groupe);
        Assert.Contains("chevron-menu @(ouvert ? \"ouvert\" : null)", groupe);
        Assert.Contains("Etat.Choisir(", groupe);   // état gardé tant que le poste est ouvert (EtatMenus)
        // Les menus qui avaient des groupes passent par le composant commun
        foreach (var f in new[] { "Mise/NavDomaine.razor", "Mise/NavAdmin.razor", "Mise/NavControleInterne.razor", "Caisse/NavCaisse.razor",
                     "Pages/Achats.razor", "Pages/Alertes.razor", "Pages/Dettes.razor", "Pages/Rapports.razor" })
            Assert.True(File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop", "Composants", f)).Contains("<GroupeMenu "),
                $"{f} : groupes sans GroupeMenu");
    }
}
