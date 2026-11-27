namespace Pharma.Desktop.Services;

/// <summary>
/// Titulaire connecté sur un PC non autorisé : Administration seulement (décision du titulaire du 2026-11-27). Le serveur ne laisse passer que
/// les routes de ces écrans (Pharma.Api, AccesAdministrationSeule.Prefixes, liste fermée testée) ; le poste n'affiche qu'eux : menu du haut réduit
/// à « Réglages », accueil = Postes autorisés, toute autre adresse ramène ici avec le bandeau. Pas de caisse, de vente, de stock ni de finances.
/// </summary>
public static class AdministrationSeule
{
    /// <summary>Écrans d'administration permis (adresse sans « / » initial), dans l'ordre du menu de l'Administration.</summary>
    public static readonly string[] Ecrans =
    [
        "admin/droits", "admin/notifications",
        "admin/imprimantes", "admin/sauvegardes", "admin/second-serveur", "admin/synchronisation",
        "admin/pharmacie", "admin/postes", "admin/offre"
    ];

    /// <summary>Écran d'arrivée : Postes autorisés (générer le code d'un poste).</summary>
    public const string Accueil = "admin/postes";

    public const string Bandeau = "PC non autorisé : Administration seulement ; autorisez ce PC dans Postes autorisés.";

    public const string Aide = "Pour autoriser ce PC : créez-le dans Postes autorisés, déconnectez-vous, puis tapez son code sur l’écran de connexion.";

    /// <summary>Écran permis (sous-chemins compris), plus le changement du mot de passe et la connexion.</summary>
    public static bool PeutOuvrir(string chemin) =>
        chemin is "mot-de-passe" || chemin.StartsWith("connexion", StringComparison.Ordinal)
        || Ecrans.Any(e => chemin == e || chemin.StartsWith(e + "/", StringComparison.Ordinal));
}
