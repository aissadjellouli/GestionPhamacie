using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Toute route de l'API exige une connexion (Exiger* ou RequireAuthorization sur la route, son groupe ou un groupe parent), sauf les
/// routes publiques listées ici (revue de code du 2026-11-22 : /psychotropes et /substitution étaient ouvertes sans jeton). Analyse du
/// code des endpoints : une route est le texte entre son Map… et la registration suivante.
/// </summary>
public class RoutesProtegeesTests
{
    /// <summary>Routes ouvertes sans jeton, voulues : connexion, état du serveur, mode démonstration.</summary>
    private static readonly HashSet<string> Publiques =
    [
        "POST /api/auth/connexion", "POST /api/auth/connexion-code",
        "GET /api/healthcheck", "GET /api/demo", "POST /api/super-admin/connexion",
    ];

    private static readonly Regex Registration = new(@"^\s*(?:var\s+(?<var>\w+)\s*=\s*)?(?<cible>\w+)\.Map(?<verbe>Get|Post|Put|Delete|Patch|Group|Methods)\(""(?<route>[^""]*)""",
        RegexOptions.Compiled);

    private static readonly Regex Garde = new(@"\.(Exiger\w*|RequireAuthorization)\(", RegexOptions.Compiled);

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    /// <summary>Routes sans garde : « VERBE /route (fichier:ligne) ».</summary>
    private static List<(string Route, string Ou)> RoutesSansGarde()
    {
        var sansGarde = new List<(string, string)>();
        foreach (var fichier in Directory.GetFiles(Path.Combine(RacineDepot(), "src", "Pharma.Api", "Endpoints"), "*.cs").Order(StringComparer.Ordinal))
        {
            var lignes = File.ReadAllLines(fichier);
            var blocs = lignes.Select((texte, i) => (Match: Registration.Match(texte), Ligne: i)).Where(x => x.Match.Success).ToList();
            // Groupes : préfixe complet et garde (sur la ligne de leur déclaration), parents suivis
            var prefixes = new Dictionary<string, string> { ["app"] = "", ["racine"] = "", ["api"] = "/api" };
            var gardes = new Dictionary<string, bool>();
            var parents = new Dictionary<string, string>();
            for (var b = 0; b < blocs.Count; b++)
            {
                var (m, debut) = blocs[b];
                var fin = b + 1 < blocs.Count ? blocs[b + 1].Ligne : lignes.Length;
                var texte = string.Join('\n', lignes[debut..fin]);
                var cible = m.Groups["cible"].Value;
                var prefixe = prefixes.GetValueOrDefault(cible, "/api");
                if (m.Groups["verbe"].Value == "Group")
                {
                    var nom = m.Groups["var"].Value;
                    prefixes[nom] = prefixe + m.Groups["route"].Value;
                    gardes[nom] = Garde.IsMatch(lignes[debut]);
                    parents[nom] = cible;
                    continue;
                }
                var groupeGarde = false;
                for (var g = cible; !groupeGarde && g is not null && gardes.ContainsKey(g); g = parents.GetValueOrDefault(g))
                    groupeGarde = gardes[g];
                var route = $"{m.Groups["verbe"].Value.ToUpperInvariant()} {prefixe}{m.Groups["route"].Value}";
                if (!groupeGarde && !Garde.IsMatch(texte) && !Publiques.Contains(route))
                    sansGarde.Add((route, $"{Path.GetFileName(fichier)}:{debut + 1}"));
            }
        }
        return sansGarde;
    }

    [Fact]
    public void Toute_route_exige_une_connexion_sauf_les_routes_publiques()
    {
        var sansGarde = RoutesSansGarde();
        Assert.True(sansGarde.Count == 0, "Routes sans Exiger* (ni sur la route ni sur son groupe) : "
            + string.Join(" ; ", sansGarde.Select(r => $"{r.Route} ({r.Ou})")));
    }

    [Fact]
    public void L_analyse_trouve_bien_les_routes()
    {
        // Garde-fou de l'analyse elle-même : elle voit les routes publiques et les groupes corrigés le 2026-11-22
        var endpoints = Path.Combine(RacineDepot(), "src", "Pharma.Api", "Endpoints");
        Assert.Contains("MapGroup(\"/psychotropes\").ExigerConnexion()", File.ReadAllText(Path.Combine(endpoints, "RegistreEndpoints.cs")));
        Assert.Contains("MapGroup(\"/substitution\").ExigerConnexion()", File.ReadAllText(Path.Combine(endpoints, "RupturesEndpoints.cs")));
        var toutes = Directory.GetFiles(endpoints, "*.cs").SelectMany(File.ReadAllLines).Count(l => Registration.IsMatch(l));
        Assert.True(toutes > 300, $"seulement {toutes} registrations vues : l'analyse ne reconnaît plus la forme du code");
    }
}
