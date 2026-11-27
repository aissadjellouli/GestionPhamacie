namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot O6 (offre de base et options, docs/offre-et-options.md) : les routes des packs portent le filtre ExigerPack (le serveur refuse lui-même
/// un pack non inclus). Retirer un filtre par erreur ouvrirait une option sans activation : le nombre de filtres par fichier est figé ici ;
/// le changer est une décision à reporter dans offre-et-options.md.
/// </summary>
public class PacksOffreRoutesTests
{
    private static readonly Dictionary<string, (string Pack, int Routes)> Attendus = new()
    {
        ["SurveillanceEndpoints.cs"] = ("ControleFraude", 7),       // contrôle interne ; tiroir, intégrité et journal fiscal restent dans la base
        ["TracabiliteEndpoints.cs"] = ("Inventaire", 3),            // le blocage d'un lot rappelé reste dans la base
        ["SerialisationEndpoints.cs"] = ("Inventaire", 4),
        ["MobileEndpoints.cs"] = ("Inventaire", 1),                 // groupe /mobile
        ["InventaireEndpoints.cs"] = ("Inventaire", 4),             // démarrage d'un inventaire tournant ; le comptage sert à l'inventaire complet
        ["MultiGrossistesEndpoints.cs"] = ("Achats", 7),            // l'envoi d'un bon par e-mail reste dans la base
        ["RupturesEndpoints.cs"] = ("Achats", 2),
        ["TresorerieEndpoints.cs"] = ("Finances", 1),
        ["SuiviDettesEndpoints.cs"] = ("Finances", 3),
        ["FinancementsEndpoints.cs"] = ("Finances", 1),
        ["PilotageFinancierEndpoints.cs"] = ("Finances", 1),
        ["PilotageEndpoints.cs"] = ("Pilotage", 1),
        ["NotificationsEndpoints.cs"] = ("Pilotage", 1),            // rapport quotidien envoyé tout de suite
        ["SecondServeurEndpoints.cs"] = ("Continuite", 1),
        ["SynchronisationEndpoints.cs"] = ("MultiOfficines", 9),    // statut et ordres déjà reçus restent (décision 3) ; réglages : désactiver permis sans G (2026-11-27), activer refusé dans la route
        ["OrdonnancesEndpoints.cs"] = ("Comptoir", 2),
    };

    [Fact]
    public void Chaque_fichier_de_pack_garde_ses_filtres()
    {
        var dossier = Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Api", "Endpoints");
        var ecarts = new List<string>();
        foreach (var (fichier, (pack, routes)) in Attendus)
        {
            var texte = File.ReadAllText(Path.Combine(dossier, fichier));
            var n = texte.Split($"ExigerPack(PacksOffre.{pack})").Length - 1 + texte.Split($"ExigerPack(Pharma.Application.Offre.PacksOffre.{pack})").Length - 1;
            if (n != routes) ecarts.Add($"{fichier} : {n} filtre(s) du pack {pack}, {routes} attendu(s)");
        }
        Assert.True(ecarts.Count == 0, "Filtres de packs changés : " + string.Join(" ; ", ecarts));
    }

    [Fact]
    public void Aucun_autre_fichier_ne_porte_de_filtre_de_pack_sans_etre_inventorie()
    {
        var dossier = Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Api", "Endpoints");
        var inconnus = Directory.GetFiles(dossier, "*.cs").Select(Path.GetFileName)
            .Where(f => File.ReadAllText(Path.Combine(dossier, f!)).Contains("ExigerPack(") && !Attendus.ContainsKey(f!)).ToList();
        Assert.True(inconnus.Count == 0, "Filtres de packs hors inventaire : " + string.Join(", ", inconnus));
    }
}
