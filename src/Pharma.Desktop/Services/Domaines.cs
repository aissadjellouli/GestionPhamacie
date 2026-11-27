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
        /// <summary>
        /// L'écran a des rubriques : il se déplie dans le menu de gauche quand il est affiché (chevron « › » replié, « ⌄ » déplié, à tout niveau :
        /// décision du titulaire du 2026-11-27). Liste fermée ci-dessous, contrôlée par un test (chaque écran qui fournit des rubriques à NavDomaine).
        /// </summary>
        public bool AvecRubriques => EcransAvecRubriques.Contains(Lien);

        /// <summary>Aucun droit demandé, ou l'un des droits donnés ; un écran « à venir » (Raison) n'est jamais permis.</summary>
        public bool Permis(SessionPoste s) => Raison is null && (Droits.Length == 0 || Droits.Any(s.A));
        public bool Designe(string chemin) => Chemins.Any(c => c == "" ? chemin == "" : chemin == c || chemin.StartsWith(c + "/", StringComparison.Ordinal));
    }

    /// <summary>Écrans dont la page fournit des rubriques au menu de gauche (NavDomaine avec contenu).</summary>
    public static readonly IReadOnlySet<string> EcransAvecRubriques = new HashSet<string>(StringComparer.Ordinal)
    {
        "", "stock", "inventaire", "stock/registre", "tresorerie", "dettes", "financements", "rapports", "pilotage-financier", "surveillance"
    };

    /// <summary>Domaine du menu du haut. <paramref name="Touche"/> : raccourci Ctrl + Maj + lettre.</summary>
    public sealed record Domaine(string Id, string Libelle, char Touche, IReadOnlyList<Ecran> Ecrans)
    {
        public IEnumerable<Ecran> Permis(SessionPoste s) => Ecrans.Where(e => e.Permis(s));
        /// <summary>PC non autorisé (titulaire, Administration seulement) : aucun domaine, le menu du haut se réduit à « Réglages ».</summary>
        public bool Visible(SessionPoste s) => !s.AdministrationSeule && Permis(s).Any();
        /// <summary>Premier écran permis : celui qu'ouvre le domaine dans le menu du haut.</summary>
        public string? Lien(SessionPoste s) => Permis(s).FirstOrDefault()?.Lien;
    }

    public static readonly IReadOnlyList<Domaine> Tous =
    [
        new("caisse", "Caisse", 'V', [new("Caisse", "caisse", ["caisse", "cloture"], [])]),
        new("stock", "Stock", 'S',
        [
            new("Alertes", "", [""], []),
            // « Gestion du stock » (décision du titulaire du 2026-11-27 ; première rubrique renommée « Liste des produits ») : plus de « STOCK › Stock › Produits » ; adresse inchangée
            new("Gestion du stock", "stock", ["stock"], []),
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
            // Bordereaux Chifa / MDN (script 110) : dépôt, rejets, paiements
            new("Bordereaux Chifa", "bordereaux-chifa", ["bordereaux-chifa"], ["chifa.bordereaux"]),
        ]),
        // Patients (2026-11-18) : fichier des patients (droits de la recherche et de la fiche patient), suivi des droits Chifa
        // expirés ou bientôt expirés (2026-11-15), patients en attente d'un produit (déplacés d'Achats, droit de vendre comme avant)
        new("patients", "Patients", 'T',
        [
            new("Fichier des patients", "patients", ["patients"], ["caisse.vendre"]),
            new("Droits expirés ou bientôt expirés", "patients/droits-chifa", ["patients/droits-chifa"], ["caisse.vendre"]),
            new("Patients en attente", "patients/attente", ["patients/attente"], ["caisse.vendre"]),
        ]),
        new("pilotage", "Pilotage", 'P',
        [
            new("Rapports", "rapports", ["rapports"], ["rapports.ventes"]),
            // Tableau de bord financier (lot 11) : titulaire ou droit de voir prix d'achat et marges
            new("Tableau de bord financier", "pilotage-financier", ["pilotage-financier"], ["role.titulaire", "rapports.marges"]),
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
    /// <remarks>PC non autorisé (titulaire, Administration seulement) : Postes autorisés.</remarks>
    public static string Accueil(SessionPoste s) =>
        s.AdministrationSeule ? AdministrationSeule.Accueil : ATableauDeBord(s) || !s.A("caisse.vendre") ? "accueil" : "caisse";
}
