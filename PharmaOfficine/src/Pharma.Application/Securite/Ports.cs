using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>Vérifie un droit côté serveur, à chaque appel.</summary>
public interface IPermissionService
{
    Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default);
}

public interface IComptesLecture
{
    /// <summary>Compte actif portant cet identifiant, sinon null.</summary>
    Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default);
}

public interface IVerificateurMotDePasse
{
    bool Verifier(string motDePasse, string hash);
}

public interface IGenerateurJeton
{
    (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet);
}
