namespace Pharma.Application.Securite;

/// <summary>DoitChangerMotDePasse : jeton sans aucun droit tant que le mot de passe provisoire n'est pas changé.</summary>
public sealed record Session(string Jeton, string NomComplet, DateTime ExpireLe, bool DoitChangerMotDePasse = false);

/// <summary>Connexion d'un employé : renvoie une session, ou null si l'identifiant ou le mot de passe est faux.</summary>
public sealed class SeConnecter(
    IComptesLecture comptes, IVerificateurMotDePasse verificateur, IGenerateurJeton jetons)
{
    public async Task<Session?> ExecuterAsync(string identifiant, string motDePasse, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrEmpty(motDePasse))
            return null;

        var compte = await comptes.TrouverActifAsync(identifiant.Trim(), ct);
        if (compte is null || !compte.Actif || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
            return null;

        await comptes.NoterConnexionAsync(compte.Id, ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet, motDePasseAChanger: compte.DoitChangerMotDePasse);
        return new Session(jeton, compte.NomComplet, expire, compte.DoitChangerMotDePasse);
    }
}
