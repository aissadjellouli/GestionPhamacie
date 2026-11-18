using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>
/// Ressaisie du mot de passe de l'employé connecté avant une action sensible (mécanisme de « Changer mon mot de passe », extrait
/// le 2026-11-18 pour « Repartir sur une nouvelle époque ») : mot de passe faux compté avec les essais de connexion du même
/// identifiant (même blocage), chaque refus inscrit au journal ; bloqué, l'action est refusée avant même la vérification.
/// </summary>
public sealed class VerificationMotDePasseActuel(IComptesLecture comptes, IVerificateurMotDePasse verificateur, IJournal journal,
    EssaisConnexion? essais = null, IHorloge? horloge = null)
{
    /// <param name="contexte">Action demandée (détail du journal en cas de refus).</param>
    /// <param name="champ">Champ de l'écran où afficher le refus.</param>
    public async Task<Utilisateur> VerifierAsync(long utilisateurId, string? motDePasse, string contexte, string champ, CancellationToken ct = default)
    {
        var compte = await comptes.ParIdAsync(utilisateurId, ct);
        if (compte is null || !compte.Actif) throw new RegleMetierException("Compte introuvable ou désactivé.");
        var maintenant = horloge?.MaintenantUtc ?? DateTime.UtcNow;
        var nom = compte.Identifiant.Trim();
        if (essais?.BloqueJusqu(nom, maintenant) is { } jusqu)
            throw new RegleMetierException(
                $"Mot de passe bloqué après {EssaisConnexion.EssaisMax} mots de passe faux : réessayez après {jusqu.ToLocalTime():HH:mm}.", champ);
        if (string.IsNullOrEmpty(motDePasse) || !verificateur.Verifier(motDePasse, compte.MotDePasseHash))
        {
            var bloque = essais?.NoterEchec(nom, maintenant) == true;
            await journal.EcrireAsync(compte.Id, bloque ? "connexion_bloquee" : "mot_de_passe_actuel_refuse", "utilisateur", compte.Id,
                new Dictionary<string, object?> { ["contexte"] = contexte }, null, ct);
            throw new RegleMetierException(bloque
                ? $"Mot de passe bloqué {EssaisConnexion.Blocage.TotalMinutes:0} minutes après {EssaisConnexion.EssaisMax} mots de passe faux."
                : "Mot de passe actuel incorrect.", champ);
        }
        essais?.NoterSucces(nom);
        return compte;
    }
}
