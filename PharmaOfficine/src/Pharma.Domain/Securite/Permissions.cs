namespace Pharma.Domain.Securite;

/// <summary>Codes des permissions, identiques à la table "permission".</summary>
public static class Permissions
{
    public const string CaisseVendre = "caisse.vendre";
    public const string CaisseSupprimerLigne = "caisse.supprimer_ligne";
    public const string CaisseAnnulerVente = "caisse.annuler_vente";
    public const string CaisseRemise = "caisse.remise";
    public const string CaisseRetourEspeces = "caisse.retour_especes";
    public const string CaisseOuvrirTiroir = "caisse.ouvrir_tiroir";
    public const string CaisseVoirEcart = "caisse.voir_ecart";
    public const string CreditAccorder = "credit.accorder";
    public const string CreditDepasserPlafond = "credit.depasser_plafond";
    public const string PsychotropesDelivrer = "psychotropes.delivrer";
    public const string PsychotropesRegistre = "psychotropes.registre";
    public const string StockConsulter = "stock.consulter";
    public const string StockAjuster = "stock.ajuster";
    public const string StockDeclarerPerime = "stock.declarer_perime";
    public const string PrixModifier = "prix.modifier";
    public const string CatalogueCreer = "catalogue.creer";
    public const string AchatsCommander = "achats.commander";
    public const string AchatsReceptionner = "achats.receptionner";
    public const string AchatsRetourFournisseur = "achats.retour_fournisseur";
    public const string ChifaImporter = "chifa.importer";
    public const string ChifaBordereaux = "chifa.bordereaux";
    public const string RapportsVentes = "rapports.ventes";
    public const string RapportsMarges = "rapports.marges";
    public const string AdminParametres = "admin.parametres";
    public const string AdminUtilisateurs = "admin.utilisateurs";
    public const string AdminSauvegardes = "admin.sauvegardes";
}
