using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Revue de code du 2026-11-27 : aucun gestionnaire « async void » dans le poste (une exception non rattrapée ferait planter l'application),
/// aucune attente bloquante (.Result, .Wait(), GetResult()) ; écran Mon offre : message de réussite seulement sur l'offre relue.
/// </summary>
public class PosteSansAttenteBloquanteTests
{
    private static string Racine => Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop");

    private static IEnumerable<string> Sources() =>
        Directory.EnumerateFiles(Racine, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".razor", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    // Lignes de code seulement (les commentaires peuvent citer « async void » pour l'interdire)
    private static IEnumerable<(string Fichier, string Ligne)> Lignes() =>
        Sources().SelectMany(f => File.ReadAllLines(f).Select(l => (Path.GetFileName(f), l.Trim())))
            .Where(x => !x.Item2.StartsWith("//", StringComparison.Ordinal) && !x.Item2.StartsWith("@*", StringComparison.Ordinal)
                        && !x.Item2.StartsWith("///", StringComparison.Ordinal) && !x.Item2.StartsWith("*", StringComparison.Ordinal));

    [Fact]
    public void Aucun_async_void_dans_le_poste()
    {
        var trouves = Lignes().Where(x => Regex.IsMatch(x.Ligne, @"\basync\s+void\b")).Select(x => $"{x.Fichier} : {x.Ligne}").ToList();
        Assert.Empty(trouves);
    }

    [Fact]
    public void Aucune_attente_bloquante_hors_continuation()
    {
        // Seule exception : la continuation de la sonde du serveur (tâche déjà terminée, jamais attendue au fil de l'interface)
        var trouves = Lignes()
            .Where(x => Regex.IsMatch(x.Ligne, @"\.Result\b(?!\s*=)|\.Wait\(\)|GetAwaiter\(\)\.GetResult\(\)"))
            .Where(x => !x.Ligne.Contains("if (t.IsCompletedSuccessfully) t.Result.Dispose();", StringComparison.Ordinal))
            .Select(x => $"{x.Fichier} : {x.Ligne}").ToList();
        Assert.Empty(trouves);
    }

    [Fact]
    public void Mise_en_page_lance_ses_taches_d_evenement_en_observant_l_erreur()
    {
        var s = File.ReadAllText(Path.Combine(Racine, "Composants", "Mise", "MiseEnPage.razor")).Replace("\r\n", "\n");
        Assert.Contains("private void SurNavigation(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e) => Lancer(ApresNavigationAsync);", s);
        Assert.Contains("private void SurSession() => Lancer(ApresSessionAsync);", s);
        Assert.Contains("catch (Exception ex) when (ex is not OutOfMemoryException) { Pharma.Desktop.App.NoterErreur(ex); }", s);
    }

    [Fact]
    public void Mon_offre_ne_decrit_que_l_offre_relue_apres_un_code_accepte()
    {
        var s = File.ReadAllText(Path.Combine(Racine, "Composants", "Pages", "AdminOffre.razor")).Replace("\r\n", "\n");
        Assert.Contains("if (await ChargerAsync() && o is { } relue) message = $\"Code accepté : {Etat(relue)}, {relue.Postes} poste(s).\";", s);
        Assert.Contains("else (erreur, message) = (null, \"Code accepté ; rechargez l’écran.\");", s);
        Assert.DoesNotContain("o!.", s);
    }
}
