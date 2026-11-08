namespace Pharma.Application.Securite;

public sealed record Session(string Jeton, string NomComplet, DateTime ExpireLe);

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

        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet);
        return new Session(jeton, compte.NomComplet, expire);
    }
}
