using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>Vérifie un droit côté serveur, à chaque appel.</summary>
public interface IPermissionService
{
    Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default);

    /// <summary>Codes des permissions de l'employé (pour adapter l'écran ; le serveur revérifie toujours).</summary>
    Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>Remise maximale (%) autorisée par le rôle de l'employé.</summary>
    Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default);

    /// <summary>Vrai pour le pharmacien titulaire (rôle système, que la matrice des droits ne peut pas attribuer).</summary>
    Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default);
}

public static class PermissionServiceExtensions
{
    public static async Task ExigerAsync(this IPermissionService droits, long utilisateurId, string permission,
        string action, CancellationToken ct = default)
    {
        if (!await droits.APermissionAsync(utilisateurId, permission, ct))
            throw new Commun.DroitRefuseException(permission, action);
    }

    /// <summary>Réservé au pharmacien titulaire, quel que soit le réglage des droits.</summary>
    public static async Task ExigerTitulaireAsync(this IPermissionService droits, long utilisateurId, string action, CancellationToken ct = default)
    {
        if (!await droits.EstTitulaireAsync(utilisateurId, ct))
            throw new Commun.DroitRefuseException(MarqueTitulaire, action);
    }

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
    Task NoterConnexionAsync(long utilisateurId, CancellationToken ct = default);
}

public interface IVerificateurMotDePasse
{
    bool Verifier(string motDePasse, string hash);
}

public interface IGenerateurJeton
{
    (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet);
}
