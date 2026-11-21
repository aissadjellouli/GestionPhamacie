using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Tuiles du poste (docs/charte-graphique.md, « Tuiles navigables », 2026-11-21) : toute tuile mène à l'écran qui détaille son
/// chiffre (ou à l'écran le plus proche), avec une infobulle qui dit où ; elle n'est un chiffre non cliquable que si l'employé n'a
/// pas le droit d'ouvrir la cible (ou qu'il n'y a encore rien à ouvrir). Une tuile s'écrit donc :
/// <c>&lt;a href class="tuile tuile-lien"&gt;</c> (modèle de l'accueil), <c>&lt;button class="tuile tuile-lien" @onclick&gt;</c>
/// (filtre de l'écran affiché), ou le composant <c>TuileLien</c> (lien contrôlé par le droit d'ouvrir la cible).
/// </summary>
public partial class TuilesNavigablesTests
{
    private static string Desktop => Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop");

    /// <summary>
    /// Liste blanche : balises « tuile » ni lien ni bouton, et pourquoi. Seule la branche non cliquable de TuileLien : elle n'est
    /// rendue que sans le droit d'ouvrir la cible (AccesEcrans.PeutOuvrir, même contrôle que le menu) ou sans écran à ouvrir.
    /// </summary>
    private static readonly (string Fichier, string Balise)[] ListeBlanche =
    [
        (Path.Combine("Composants", "Mise", "TuileLien.razor"), "div"),
    ];

    private static IEnumerable<string> FichiersRazor() =>
        Directory.GetFiles(Desktop, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"));

    [Fact]
    public void Toute_tuile_du_poste_est_un_lien_ou_un_bouton_de_navigation()
    {
        var fautives = new List<string>();
        var vues = 0;
        foreach (var f in FichiersRazor())
        {
            var relatif = Path.GetRelativePath(Desktop, f);
            var texte = File.ReadAllText(f);
            foreach (Match m in BaliseAvecClasse().Matches(texte))
            {
                var classes = m.Groups["classe"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!classes.Contains("tuile")) continue;
                vues++;
                var balise = m.Groups["balise"].Value;
                var attributs = m.Value;
                var lien = balise == "a" && attributs.Contains("href=", StringComparison.Ordinal) && classes.Contains("tuile-lien");
                var bouton = balise == "button" && attributs.Contains("@onclick", StringComparison.Ordinal) && classes.Contains("tuile-lien")
                             && attributs.Contains("type=\"button\"", StringComparison.Ordinal) && attributs.Contains("title=", StringComparison.Ordinal);
                if (lien || bouton || ListeBlanche.Contains((relatif, balise))) continue;
                var ligne = texte[..m.Index].Count(c => c == '\n') + 1;
                fautives.Add($"{relatif}:{ligne} <{balise} class=\"{m.Groups["classe"].Value}\">");
            }
        }
        Assert.True(vues > 0, "Aucune tuile trouvée : le test ne lit plus les bons fichiers");
        Assert.True(fautives.Count == 0,
            "Tuiles non navigables (utiliser <TuileLien Lien=… Infobulle=…>, un <a class=\"tuile tuile-lien\"> ou un <button type=\"button\" class=\"tuile tuile-lien\" title=… @onclick>) : "
            + string.Join(" ; ", fautives));
    }

    [Fact]
    public void Le_lien_d_une_TuileLien_est_controle_par_le_droit_d_ouvrir_la_cible()
    {
        var composant = File.ReadAllText(Path.Combine(Desktop, "Composants", "Mise", "TuileLien.razor"));
        var lien = composant.IndexOf("<a href=", StringComparison.Ordinal);
        var garde = composant.IndexOf("AccesEcrans.PeutOuvrir(Session, lien)", StringComparison.Ordinal);
        Assert.True(garde >= 0 && garde < lien, "TuileLien : le lien doit être rendu seulement si AccesEcrans.PeutOuvrir(Session, lien)");
        Assert.Contains("class=\"tuile tuile-lien @Classe\" title=", composant);

        // Même liste que la garde de la mise en page : un écran réservé n'a qu'une description
        var mise = File.ReadAllText(Path.Combine(Desktop, "Composants", "Mise", "MiseEnPage.razor"));
        Assert.Contains("AccesEcrans.Reserve(Chemin)", mise);
        Assert.DoesNotContain("EcransReserves =", mise);
    }

    [Fact]
    public void Chaque_TuileLien_dit_ou_elle_mene_et_vise_une_route_du_poste()
    {
        var routes = FichiersRazor().SelectMany(f => Route().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value.Trim('/')))
            .ToHashSet(StringComparer.Ordinal);
        var fautes = new List<string>();
        var usages = 0;
        foreach (var f in FichiersRazor())
        {
            var relatif = Path.GetRelativePath(Desktop, f);
            foreach (Match m in UsageTuileLien().Matches(File.ReadAllText(f)))
            {
                usages++;
                var balise = m.Value;
                if (!balise.Contains("Infobulle=", StringComparison.Ordinal)) fautes.Add($"{relatif} : TuileLien sans Infobulle");
                if (!balise.Contains("Lien=", StringComparison.Ordinal)) fautes.Add($"{relatif} : TuileLien sans Lien");
                // Adresses écrites en clair (y compris dans une expression) : la route doit exister dans le poste
                foreach (Match a in AdresseLien().Matches(balise))
                {
                    var adresse = a.Groups["adresse"].Value;
                    var chemin = adresse.Split('?', '#')[0];
                    if (chemin.StartsWith("./", StringComparison.Ordinal)) chemin = chemin[2..];
                    chemin = chemin.Trim('/');
                    if (!routes.Contains(chemin)) fautes.Add($"{relatif} : « {adresse} » ne mène à aucune route du poste");
                }
            }
        }
        Assert.True(usages > 0, "Aucune TuileLien trouvée");
        Assert.True(fautes.Count == 0, string.Join(" ; ", fautes));
    }

    [GeneratedRegex(@"<(?<balise>[a-zA-Z][\w]*)\b(?:[^>""]|""[^""]*"")*?\bclass=""(?<classe>[^""]*)""(?:[^>""]|""[^""]*"")*>")]
    private static partial Regex BaliseAvecClasse();

    [GeneratedRegex(@"<TuileLien\b(?:[^>""]|""[^""]*"")*>")]
    private static partial Regex UsageTuileLien();

    /// <summary>Adresse en clair dans l'attribut Lien, ou chaîne d'une expression Lien="@(… ? "x" : "y")".</summary>
    [GeneratedRegex(@"(?:\bLien=""|"")(?<adresse>(?:\./)?[a-z][a-z0-9/-]*(?:\?[^""#\s]*)?(?:#[\w-]+)?|\./\?[^""\s]*)""")]
    private static partial Regex AdresseLien();

    [GeneratedRegex(@"@page\s+""([^""]*)""")]
    private static partial Regex Route();
}
