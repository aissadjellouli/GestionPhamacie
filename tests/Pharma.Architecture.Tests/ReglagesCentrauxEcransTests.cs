using System.Text.RegularExpressions;
using Pharma.Domain.Reglages;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Mutualisation des réglages, lots M5 et M7 : les 11 tables ont chacune un écran qui affiche le bandeau « serveur central » (composant ReglageCentral), et la
/// route de lecture de cet écran annonce l'état de la table par l'en-tête « X-Reglage-Central » (sans appel de plus). Si une table était oubliée,
/// l'écran ne grisera rien alors que le serveur central l'a imposée (le refus 409 resterait la seule garde).
/// </summary>
public partial class ReglagesCentrauxEcransTests
{
    private static readonly Dictionary<string, string> Constantes = new()
    {
        [TablesMutualisables.Fiscal] = nameof(TablesMutualisables.Fiscal),
        [TablesMutualisables.AlertesPsychotropes] = nameof(TablesMutualisables.AlertesPsychotropes),
        [TablesMutualisables.Tracabilite] = nameof(TablesMutualisables.Tracabilite),
        [TablesMutualisables.Serialisation] = nameof(TablesMutualisables.Serialisation),
        [TablesMutualisables.Substitution] = nameof(TablesMutualisables.Substitution),
        [TablesMutualisables.Motifs] = nameof(TablesMutualisables.Motifs),
        [TablesMutualisables.FormulaireSms] = nameof(TablesMutualisables.FormulaireSms),
        [TablesMutualisables.Surveillance] = nameof(TablesMutualisables.Surveillance),
        [TablesMutualisables.Alertes] = nameof(TablesMutualisables.Alertes),
        [TablesMutualisables.Inventaire] = nameof(TablesMutualisables.Inventaire),
        [TablesMutualisables.Remises] = nameof(TablesMutualisables.Remises)
    };

    [Fact]
    public void Les_onze_tables_ont_un_bandeau_serveur_central_sur_leur_ecran()
    {
        var composants = Path.Combine(RacineDepot(), "src", "Pharma.Desktop", "Composants");
        var affichees = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => BandeauTable().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToHashSet();
        Assert.Equal(TablesMutualisables.Toutes.Order(), affichees.Order());
    }

    [Fact]
    public void La_route_de_lecture_de_chaque_ecran_annonce_l_etat_de_sa_table_dans_l_en_tete()
    {
        Assert.Equal(11, Constantes.Count);
        var endpoints = Path.Combine(RacineDepot(), "src", "Pharma.Api", "Endpoints");
        var annoncees = Directory.GetFiles(endpoints, "*.cs")
            .SelectMany(f => EnTete().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .ToList();
        Assert.Equal(Constantes.Values.Order(), annoncees.Order());   // une fois chacune, aucune oubliée, aucune en double
    }

    [Fact]
    public void Les_routes_de_reglage_fiscal_et_de_tracabilite_sont_reservees_au_titulaire()
    {
        var texte = File.ReadAllText(Path.Combine(RacineDepot(), "src", "Pharma.Api", "Endpoints", "ReglagesMutualisesEndpoints.cs"));
        foreach (var route in new[] { "\"/fiscal/parametres\"", "\"/tracabilite/parametres\"" })
            foreach (var verbe in new[] { "MapGet", "MapPut" })
            {
                var debut = texte.IndexOf($"api.{verbe}({route}", StringComparison.Ordinal);
                Assert.True(debut >= 0, $"{verbe} {route} absent");
                var fin = texte.IndexOf("api.Map", debut + 5, StringComparison.Ordinal);   // jusqu'à la route suivante (ou la fin du fichier)
                Assert.Contains(".ExigerTitulaire()", texte[debut..(fin < 0 ? texte.Length : fin)]);
            }
    }

    [GeneratedRegex(@"<ReglageCentral\s+Table=""([a-z_]+)""")] private static partial Regex BandeauTable();
    [GeneratedRegex(@"AvecEtatReglageCentral\((?:Pharma\.Domain\.Reglages\.)?TablesMutualisables\.(\w+)\)")] private static partial Regex EnTete();

    private static string RacineDepot()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } racine) return racine;
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
