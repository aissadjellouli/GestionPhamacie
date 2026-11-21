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
    IGenerateurJeton jetons, IJournal journal, EssaisConnexion? essais = null, IHorloge? horloge = null, Demo.ModeDemo? demo = null)
{
    /// <summary>
    /// Mot de passe actuel faux : compté avec les essais de connexion du même identifiant (même blocage), chaque refus inscrit
    /// au journal ; bloqué, le changement est refusé avant même la vérification.
    /// </summary>
    public async Task<Session> ExecuterAsync(long utilisateurId, string? actuel, string? nouveau, string? confirmation, CancellationToken ct = default)
    {
        // Démonstration : les trois comptes communs gardent le mot de passe de démonstration (docs/regles-metier.md)
        if (demo is { Actif: true } && await comptes.ParIdAsync(utilisateurId, ct) is { } demande && Demo.ModeDemo.EstCompteDemo(demande.Identifiant))
            demo.Exiger("changement du mot de passe d'un compte de démonstration");
        // Vérification du mot de passe actuel partagée avec les actions sensibles qui le font ressaisir (VerificationMotDePasseActuel)
        var compte = await new VerificationMotDePasseActuel(comptes, verificateur, journal, essais, horloge)
            .VerifierAsync(utilisateurId, actuel, "changement_mot_de_passe", "mot_de_passe_actuel", ct);
        RegleMotDePasse.Verifier(nouveau, confirmation, actuel);

        await comptes.ChangerMotDePasseAsync(compte.Id, hacheur.Hacher(nouveau!), ct);
        await journal.EcrireAsync(compte.Id, "mot_de_passe_change", "utilisateur", compte.Id,
            new Dictionary<string, object?> { ["premiere_connexion"] = compte.DoitChangerMotDePasse }, null, ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet);
        return new Session(jeton, compte.NomComplet, expire);
    }
}
