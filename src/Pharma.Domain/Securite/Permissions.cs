namespace Pharma.Domain.Securite;

/// <summary>Codes des permissions, identiques à la table "permission".</summary>
public static class Permissions
{
    public const string CaisseVendre = "caisse.vendre";
    public const string CaisseSupprimerLigne = "caisse.supprimer_ligne";
    public const string CaisseAnnulerVente = "caisse.annuler_vente";
    public const string CaisseRemise = "caisse.remise";
    /// <summary>Créer, modifier et arrêter les remises automatiques (réglé sur l'employé par le titulaire).</summary>
    public const string CaisseRemisesAuto = "caisse.remises_auto";
    public const string CaisseRetourEspeces = "caisse.retour_especes";
    public const string CaisseOuvrirTiroir = "caisse.ouvrir_tiroir";
    public const string CaisseVoirEcart = "caisse.voir_ecart";
    /// <summary>Faire la relève et clôturer la caisse (085) ; un vendeur le fait avec le code comptoir d'un ayant droit.</summary>
    public const string CaisseCloturer = "caisse.cloturer";
    /// <summary>Vendre sans ordonnance un médicament de liste I ou II (jamais un psychotrope ni un stupéfiant).</summary>
    public const string CaisseVendreSansOrdonnance = "caisse.vendre_sans_ordonnance";
    public const string CreditAccorder = "credit.accorder";
    public const string CreditDepasserPlafond = "credit.depasser_plafond";
    public const string PsychotropesDelivrer = "psychotropes.delivrer";
    public const string PsychotropesRegistre = "psychotropes.registre";
    /// <summary>Forcer une alerte du registre dont la règle exige un valideur (script 117) ; accordable par code comptoir.</summary>
    public const string PsychotropesForcerAlerte = "psychotropes.forcer_alerte";
    public const string StockConsulter = "stock.consulter";
    public const string StockAjuster = "stock.ajuster";
    public const string StockDeclarerPerime = "stock.declarer_perime";
    /// <summary>Donner un échantillon ou une gratuité (don à un nécessiteux) ; jamais un produit sous registre.</summary>
    public const string StockDonner = "stock.donner";
    /// <summary>Corriger une entrée de stock (PPA, n° de lot, péremption, SHP, quantité ; 103) ; jamais autorisable par code comptoir.</summary>
    public const string StockCorrigerEntree = "stock.corriger_entree";
    public const string PrixModifier = "prix.modifier";
    public const string ProduitsBloquer = "produits.bloquer";
    public const string CatalogueCreer = "catalogue.creer";
    public const string AchatsCommander = "achats.commander";
    public const string AchatsReceptionner = "achats.receptionner";
    public const string AchatsRegler = "achats.regler";
    public const string TresorerieGerer = "tresorerie.gerer";
    public const string FacturesGerer = "factures.gerer";
    public const string AchatsRetourFournisseur = "achats.retour_fournisseur";
    /// <summary>Valider un écart entre le comptage (réception à l'aveugle) et la facture d'une livraison.</summary>
    public const string AchatsValiderEcart = "achats.valider_ecart";
    public const string ChifaImporter = "chifa.importer";
    public const string ChifaBordereaux = "chifa.bordereaux";
    /// <summary>Exclure une vente Chifa du traitement, ou la rétablir (script 100) ; jamais autorisable par code comptoir.</summary>
    public const string ChifaExclure = "chifa.exclure";
    public const string RapportsVentes = "rapports.ventes";
    public const string RapportsMarges = "rapports.marges";
    public const string AdminParametres = "admin.parametres";
    public const string AdminUtilisateurs = "admin.utilisateurs";
    public const string AdminSauvegardes = "admin.sauvegardes";
    /// <summary>Valider les doublons d'import de patients (093) ; décision d'administration, jamais autorisable par code comptoir.</summary>
    public const string RepriseDoublons = "reprise.doublons";
}
