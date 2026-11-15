namespace Pharma.Desktop.Services;

/// <summary>
/// Navigation en deux niveaux (validée le 2026-11-13, docs/navigation.md) : en haut les domaines, à gauche les écrans du domaine,
/// l'écran affiché déplié avec ses rubriques. Une seule description, lue par l'en-tête, le menu de gauche (NavDomaine),
/// la recherche (Ctrl + K) et l'accueil. Un écran sans droit n'apparaît pas ; un domaine sans écran permis non plus.
/// </summary>
public static class Domaines
{
    /// <summary>Écran d'un domaine. <paramref name="Chemins"/> : chemins (sans « / » initial) qui le désignent, sous-chemins compris.</summary>
    public sealed record Ecran(string Libelle, string Lien, string[] Chemins, string[] Droits, string? Raison = null)
    {
        /// <summary>Aucun droit demandé, ou l'un des droits donnés ; un écran « à venir » (Raison) n'est jamais permis.</summary>
        public bool Permis(SessionPoste s) => Raison is null && (Droits.Length == 0 || Droits.Any(s.A));
        public bool Designe(string chemin) => Chemins.Any(c => c == "" ? chemin == "" : chemin == c || chemin.StartsWith(c + "/", StringComparison.Ordinal));
    }

    /// <summary>Domaine du menu du haut. <paramref name="Touche"/> : raccourci Ctrl + Maj + lettre.</summary>
    public sealed record Domaine(string Id, string Libelle, char Touche, IReadOnlyList<Ecran> Ecrans)
    {
        public IEnumerable<Ecran> Permis(SessionPoste s) => Ecrans.Where(e => e.Permis(s));
        public bool Visible(SessionPoste s) => Permis(s).Any();
        /// <summary>Premier écran permis : celui qu'ouvre le domaine dans le menu du haut.</summary>
        public string? Lien(SessionPoste s) => Permis(s).FirstOrDefault()?.Lien;
    }

    public static readonly IReadOnlyList<Domaine> Tous =
    [
        new("caisse", "Caisse", 'V', [new("Caisse", "caisse", ["caisse", "cloture"], [])]),
        new("stock", "Stock", 'S',
        [
            new("Alertes", "", [""], []),
            new("Stock", "stock", ["stock"], []),
            new("Inventaire", "inventaire", ["inventaire"], []),
            new("Registre des psychotropes", "stock/registre", ["stock/registre"], ["psychotropes.registre"]),
            new("Étiquettes internes", "stock/etiquettes", ["stock/etiquettes"], ["stock.consulter", "achats.receptionner"]),
            new("Traçabilité et rappel de lot", "tracabilite", ["tracabilite"], []),
        ]),
        new("achats", "Achats", 'H', [new("Achats", "achats", ["achats"], ["achats.commander", "achats.receptionner", "achats.retour_fournisseur"])]),
        new("finances", "Finances", 'F',
        [
            new("Factures", "factures", ["factures"], ["factures.gerer"]),
            new("Trésorerie", "tresorerie", ["tresorerie"], ["tresorerie.gerer"]),
            new("Dettes et créances", "dettes", ["dettes"], ["tresorerie.gerer"]),
            new("Financements", "financements", ["financements"], ["role.titulaire"]),
            // Circuit « la caisse d'abord » (2026-11-15) : ventes Chifa en attente de volet, volets rattachés avec écart
            new("Chifa", "chifa", ["chifa"], ["caisse.vendre"]),
        ]),
        new("pilotage", "Pilotage", 'P',
        [
            new("Rapports", "rapports", ["rapports"], ["rapports.ventes"]),
            new("Contrôle interne", "surveillance", ["surveillance"], ["role.titulaire"]),
        ]),
    ];

    /// <summary>Domaine et écran du chemin affiché (sans « / » initial ni paramètres) ; (null, null) hors domaine (accueil, guide, administration…).</summary>
    public static (Domaine? Domaine, Ecran? Ecran) De(string chemin)
    {
        // L'écran le plus précis d'abord (« stock/registre » avant « stock »)
        var trouves = Tous.SelectMany(d => d.Ecrans.Where(e => e.Raison is null && e.Designe(chemin)).Select(e => (d, e)))
            .OrderByDescending(x => x.e.Chemins.Max(c => c.Length)).ToList();
        return trouves.Count == 0 ? (null, null) : (trouves[0].d, trouves[0].e);
    }

    /// <summary>
    /// Droits de gestion (achats, stock, rapports, trésorerie, administration…) : l'employé arrive sur le tableau de bord ;
    /// sans aucun de ces droits (vendeur, stagiaire), directement sur la Caisse.
    /// </summary>
    private static readonly string[] DroitsDeGestion =
        ["role.titulaire", "achats.commander", "achats.valider_ecart", "stock.ajuster", "rapports.ventes", "tresorerie.gerer", "factures.gerer", "admin.parametres"];

    public static bool ATableauDeBord(SessionPoste s) => DroitsDeGestion.Any(s.A);

    /// <summary>Écran d'arrivée après la connexion et au clic sur le logo.</summary>
    public static string Accueil(SessionPoste s) => ATableauDeBord(s) || !s.A("caisse.vendre") ? "accueil" : "caisse";
}
