using Pharma.Application.Administration;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>
/// L'employé connecté change son propre mot de passe (l'actuel est exigé). Obligatoire à la première connexion avec un
/// mot de passe provisoire ; ensuite possible à tout moment. Un nouveau jeton, avec les droits du rôle, est délivré.
/// </summary>
public sealed class ChangerMonMotDePasse(IComptesLecture comptes, IVerificateurMotDePasse verificateur, IHacheurMotDePasse hacheur,
    IGenerateurJeton jetons, IJournal journal)
{
    public async Task<Session> ExecuterAsync(long utilisateurId, string? actuel, string? nouveau, string? confirmation, CancellationToken ct = default)
    {
        var compte = await comptes.ParIdAsync(utilisateurId, ct);
        if (compte is null || !compte.Actif) throw new RegleMetierException("Compte introuvable ou désactivé.");
        if (string.IsNullOrEmpty(actuel) || !verificateur.Verifier(actuel, compte.MotDePasseHash))
            throw new RegleMetierException("Mot de passe actuel incorrect.");
        RegleMotDePasse.Verifier(nouveau, confirmation, actuel);

        await comptes.ChangerMotDePasseAsync(compte.Id, hacheur.Hacher(nouveau!), ct);
        await journal.EcrireAsync(compte.Id, "mot_de_passe_change", "utilisateur", compte.Id,
            new Dictionary<string, object?> { ["premiere_connexion"] = compte.DoitChangerMotDePasse }, null, ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet);
        return new Session(jeton, compte.NomComplet, expire);
    }
}
