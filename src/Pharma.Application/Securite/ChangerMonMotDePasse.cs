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
    IGenerateurJeton jetons, IJournal journal, EssaisConnexion? essais = null, IHorloge? horloge = null)
{
    /// <summary>
    /// Mot de passe actuel faux : compté avec les essais de connexion du même identifiant (même blocage), chaque refus inscrit
    /// au journal ; bloqué, le changement est refusé avant même la vérification.
    /// </summary>
    public async Task<Session> ExecuterAsync(long utilisateurId, string? actuel, string? nouveau, string? confirmation, CancellationToken ct = default)
    {
        var compte = await comptes.ParIdAsync(utilisateurId, ct);
        if (compte is null || !compte.Actif) throw new RegleMetierException("Compte introuvable ou désactivé.");
        var maintenant = horloge?.MaintenantUtc ?? DateTime.UtcNow;
        var nom = compte.Identifiant.Trim();
        if (essais?.BloqueJusqu(nom, maintenant) is { } jusqu)
            throw new RegleMetierException(
                $"Mot de passe bloqué après {EssaisConnexion.EssaisMax} mots de passe faux : réessayez après {jusqu.ToLocalTime():HH:mm}.", "mot_de_passe_actuel");
        if (string.IsNullOrEmpty(actuel) || !verificateur.Verifier(actuel, compte.MotDePasseHash))
        {
            var bloque = essais?.NoterEchec(nom, maintenant) == true;
            await journal.EcrireAsync(compte.Id, bloque ? "connexion_bloquee" : "mot_de_passe_actuel_refuse", "utilisateur", compte.Id,
                new Dictionary<string, object?> { ["contexte"] = "changement_mot_de_passe" }, null, ct);
            throw new RegleMetierException(bloque
                ? $"Mot de passe bloqué {EssaisConnexion.Blocage.TotalMinutes:0} minutes après {EssaisConnexion.EssaisMax} mots de passe faux."
                : "Mot de passe actuel incorrect.", "mot_de_passe_actuel");
        }
        essais?.NoterSucces(nom);
        RegleMotDePasse.Verifier(nouveau, confirmation, actuel);

        await comptes.ChangerMotDePasseAsync(compte.Id, hacheur.Hacher(nouveau!), ct);
        await journal.EcrireAsync(compte.Id, "mot_de_passe_change", "utilisateur", compte.Id,
            new Dictionary<string, object?> { ["premiere_connexion"] = compte.DoitChangerMotDePasse }, null, ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet);
        return new Session(jeton, compte.NomComplet, expire);
    }
}
