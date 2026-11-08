using System.Globalization;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Textes affichés à partir des codes renvoyés par l'API.</summary>
public static class Libelles
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");

    public static string Dinars(decimal montant, int decimales = 0) =>
        montant.ToString(decimales == 0 ? "N0" : "N2", Fr) + " DA";

    public static string Date(DateOnly d) => d.ToString("dd/MM/yyyy", Fr);
    public static string MoisAnnee(DateOnly d) => d.ToString("MM/yyyy", Fr);
    public static string DateLocale(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy", Fr);
    public static string DateHeureLocale(DateTime utc) => utc.ToLocalTime().ToString("dd/MM/yyyy 'à' HH:mm", Fr);

    public static string Aujourdhui()
    {
        var texte = DateTime.Now.ToString("dddd d MMMM yyyy", Fr);
        return char.ToUpper(texte[0], Fr) + texte[1..];
    }

    // ---- Alertes ----------------------------------------------------------

    public static string Niveau(string niveau) => niveau switch
    {
        "urgent" => "Urgent",
        "attention" => "Attention",
        _ => "Info"
    };

    /// <summary>Classe CSS de la pastille : urgent, attention ou info.</summary>
    public static string ClasseNiveau(string niveau) => niveau is "urgent" or "attention" ? niveau : "info";

    public static string TypeAlerte(AlerteDto a) => a.Type switch
    {
        "peremption" => a.Niveau == "urgent" ? "Péremption imminente" : "Péremption proche",
        "perime" => "Périmé",
        "risque_ecoulement" => "Risque d’écoulement",
        "rupture" => "Rupture",
        "stock_critique" => "Stock critique",
        "dormant" => "Produit dormant",
        "surstock" => "Surstock",
        _ => a.Type
    };

    public static bool EstPeremption(AlerteDto a) => a.Type is "peremption" or "perime" or "risque_ecoulement";

    public static string TitreAlerte(AlerteDto a) =>
        a.NumeroLot is null ? a.Produit : $"{a.Produit} · lot {a.NumeroLot}";

    /// <summary>Le message SQL commence par le produit (et le lot) : on l'enlève, le titre les affiche déjà.</summary>
    public static string DetailAlerte(AlerteDto a)
    {
        string[] prefixes = a.NumeroLot is null
            ? [$"{a.Produit} : ", $"{a.Produit} "]
            : [$"{a.Produit}, lot {a.NumeroLot} : ", $"{a.Produit} : "];
        foreach (var p in prefixes)
            if (a.Message.StartsWith(p, StringComparison.Ordinal) && a.Message.Length > p.Length)
            {
                var reste = a.Message[p.Length..];
                return char.ToUpper(reste[0], Fr) + reste[1..];
            }
        return a.Message;
    }

    public static string? LibelleValeur(AlerteDto a) => a.Type switch
    {
        "peremption" or "risque_ecoulement" => "Valeur à risque",
        "perime" => "Perte",
        _ => null
    };

    /// <summary>Action principale proposée par la maquette pour chaque type d'alerte.</summary>
    public static string? ActionAlerte(AlerteDto a) => a.Type switch
    {
        "peremption" or "risque_ecoulement" => "Retour fournisseur",
        "perime" => "Retirer du stock",
        "rupture" => "Commander",
        "stock_critique" => "Ajouter à la commande",
        "dormant" => "Mettre en avant",
        _ => null
    };

    // ---- Stock ------------------------------------------------------------

    public static string StatutStock(string statut) => statut switch
    {
        "rupture" => "Rupture",
        "critique" => "Critique",
        "dormant" => "Dormant",
        "surstock" => "Surstock",
        _ => "Normal"
    };

    public static string ClasseStatutStock(string statut) => statut switch
    {
        "rupture" => "urgent",
        "critique" => "attention",
        "dormant" or "surstock" => "info",
        _ => "ok"
    };

    public static bool EstSousRegistre(string classe) => classe is "psychotrope" or "stupefiant";

    // ---- Traçabilité ------------------------------------------------------

    public static string StatutBoite(string statut) => statut switch
    {
        "en_stock" => "En stock",
        "vendue" => "Vendue",
        "retournee_fournisseur" => "Retournée au fournisseur",
        "detruite" => "Détruite",
        "retour_client" => "Retour client",
        _ => statut
    };

    public static string Etape(string type) => type switch
    {
        "reception" => "Réception",
        "vente" => "Vente",
        "retour_client" => "Retour client",
        "retour_fournisseur" => "Retour fournisseur",
        "destruction" => "Destruction",
        "blocage" => "Blocage",
        "correction" => "Correction",
        _ => type
    };
}
