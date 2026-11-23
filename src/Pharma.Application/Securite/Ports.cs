using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>Vérifie un droit côté serveur, à chaque appel.</summary>
public interface IPermissionService
{
    Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default);

    /// <summary>Codes des permissions de l'employé (pour adapter l'écran ; le serveur revérifie toujours).</summary>
    Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>Remise maximale (%) que l'employé accorde seul : réglée sur l'employé par le titulaire (titulaire : 100).</summary>
    Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>
    /// Remise de ce pourcentage permise : dans le maximum de l'employé, sinon autorisée sur place par le code d'un employé dont le
    /// maximum la couvre (le poste le demande après un refus du droit caisse.remise).
    /// </summary>
    async Task<bool> RemiseAutoriseeAsync(long utilisateurId, decimal pourcentage, CancellationToken ct = default) =>
        await RemiseMaxAsync(utilisateurId, ct) >= pourcentage;

    /// <summary>Vrai pour le pharmacien titulaire (rôle système, que la matrice des droits ne peut pas attribuer).</summary>
    Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>
    /// Libellé du droit pour les messages (« Gérer les bordereaux et rejets » et non « chifa.bordereaux », 2026-11-22) ;
    /// droit inconnu : le code lui-même.
    /// </summary>
    Task<string> LibelleAsync(string permission, CancellationToken ct = default) => Task.FromResult(permission);
}

public static class PermissionServiceExtensions
{
    public static async Task ExigerAsync(this IPermissionService droits, long utilisateurId, string permission,
        string action, CancellationToken ct = default)
    {
        if (!await droits.APermissionAsync(utilisateurId, permission, ct))
            throw new Commun.DroitRefuseException(permission, action,
                $"Le droit « {await droits.LibelleAsync(permission, ct)} » est nécessaire pour {action}.");
    }

    /// <summary>Réservé au pharmacien titulaire, quel que soit le réglage des droits.</summary>
    public static async Task ExigerTitulaireAsync(this IPermissionService droits, long utilisateurId, string action, CancellationToken ct = default)
    {
        if (!await droits.EstTitulaireAsync(utilisateurId, ct))
            throw new Commun.DroitRefuseException(MarqueTitulaire, action);
    }

    /// <summary>
    /// Valeurs au prix d'achat (stock, péremption, pertes) : réservées au droit « rapports.marges » (comme la marge), le titulaire
    /// voit tout. Sans le droit, le serveur ne les envoie pas (null).
    /// </summary>
    public static async Task<bool> VoitPrixAchatAsync(this IPermissionService droits, long utilisateurId, CancellationToken ct = default) =>
        await droits.EstTitulaireAsync(utilisateurId, ct) || await droits.APermissionAsync(utilisateurId, Permissions.RapportsMarges, ct);

    /// <summary>Ajouté à la liste des droits envoyée au poste, pour afficher les écrans du titulaire.</summary>
    public const string MarqueTitulaire = "role.titulaire";
}

public sealed class ListerMesPermissions(IPermissionService droits)
{
    public async Task<IReadOnlyList<string>> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        var liste = (await droits.ListerAsync(utilisateurId, ct)).ToList();
        if (await droits.EstTitulaireAsync(utilisateurId, ct)) liste.Add(PermissionServiceExtensions.MarqueTitulaire);
        return liste;
    }
}

public interface IComptesLecture
{
    /// <summary>Compte actif portant cet identifiant, sinon null.</summary>
    Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default);

    /// <summary>Date de dernière connexion (affichée dans l'administration des utilisateurs).</summary>
    /// <summary>Connexion réussie : dernière connexion, et trace du poste et de la méthode (mot_de_passe ou code) pour la surveillance.</summary>
    Task NoterConnexionAsync(long utilisateurId, string? poste, string methode, CancellationToken ct = default);

    Task<Utilisateur?> ParIdAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>Nouveau mot de passe (haché) choisi par l'employé ; lève l'obligation de le changer.</summary>
    Task ChangerMotDePasseAsync(long utilisateurId, string hash, CancellationToken ct = default);
}

public interface IVerificateurMotDePasse
{
    bool Verifier(string motDePasse, string hash);

    /// <summary>
    /// Identifiant inconnu : vérification sur un hachage factice, pour un temps de réponse identique à celui d'un compte existant
    /// (la durée ne révèle pas quels identifiants existent).
    /// </summary>
    void VerifierFactice(string motDePasse) { }
}

public interface IGenerateurJeton
{
    /// <param name="comptoir">Connexion par code comptoir : jeton limité aux droits du comptoir.</param>
    /// <param name="motDePasseAChanger">Mot de passe provisoire : jeton sans aucun droit, sauf changer son mot de passe.</param>
    /// <param name="poste">Nom du poste où l'employé se connecte : lié au jeton, une requête qui annonce un autre poste est refusée.</param>
    (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet, bool comptoir = false, bool motDePasseAChanger = false, string? poste = null);
}

/// <summary>Employés actifs qui détiennent un droit (pour dire au comptoir qui peut prendre le relais).</summary>
public interface IDetenteursDroit
{
    Task<IReadOnlyList<DetenteurDroit>> ListerAsync(string permission, CancellationToken ct = default);
}

public sealed record DetenteurDroit(string NomComplet, bool CodeComptoir);
