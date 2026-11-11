using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Securite;

/// <summary>DoitChangerMotDePasse : jeton sans aucun droit tant que le mot de passe provisoire n'est pas changé.</summary>
public sealed record Session(string Jeton, string NomComplet, DateTime ExpireLe, bool DoitChangerMotDePasse = false);

/// <summary>
/// Connexion d'un employé : renvoie une session, ou null si l'identifiant ou le mot de passe est faux.
/// Après <see cref="EssaisConnexion.EssaisMax"/> mots de passe faux, l'identifiant est bloqué quelques minutes
/// (même pour un identifiant inconnu : la réponse ne révèle pas quels comptes existent) ; le blocage d'un compte
/// existant est inscrit au journal.
/// </summary>
public sealed class SeConnecter(
    IComptesLecture comptes, IVerificateurMotDePasse verificateur, IGenerateurJeton jetons,
    EssaisConnexion? essais = null, IHorloge? horloge = null, IJournal? journal = null, Surveillance.Surveiller? surveillance = null)
{
    public async Task<Session?> ExecuterAsync(string identifiant, string motDePasse, CancellationToken ct = default, string? poste = null)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrEmpty(motDePasse))
            return null;

        var nom = identifiant.Trim();
        var maintenant = horloge?.MaintenantUtc ?? DateTime.UtcNow;
        if (essais?.BloqueJusqu(nom, maintenant) is { } jusqu)
            throw new RegleMetierException(
                $"Connexion bloquée après {EssaisConnexion.EssaisMax} mots de passe faux : réessayez après {jusqu.ToLocalTime():HH:mm}.");

        var compte = await comptes.TrouverActifAsync(nom, ct);
        if (compte is null || !compte.Actif || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
        {
            if (essais is not null && essais.NoterEchec(nom, maintenant))
            {
                if (compte is not null && journal is not null)
                    await journal.EcrireAsync(compte.Id, "connexion_bloquee", "utilisateur", compte.Id,
                        new Dictionary<string, object?> { ["essais"] = EssaisConnexion.EssaisMax }, null, ct);
                throw new RegleMetierException(
                    $"Connexion bloquée {EssaisConnexion.Blocage.TotalMinutes:0} minutes après {EssaisConnexion.EssaisMax} mots de passe faux.");
            }
            return null;
        }

        essais?.NoterSucces(nom);
        await comptes.NoterConnexionAsync(compte.Id, string.IsNullOrWhiteSpace(poste) ? null : poste.Trim(), "mot_de_passe", ct);
        // Surveillance : même employé actif sur deux postes à la fois
        if (surveillance is not null) await surveillance.ExecuterAsync(ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet, motDePasseAChanger: compte.DoitChangerMotDePasse);
        return new Session(jeton, compte.NomComplet, expire, compte.DoitChangerMotDePasse);
    }
}
