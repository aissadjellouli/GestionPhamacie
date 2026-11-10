using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>Compte vu pour une connexion par code comptoir (code haché, essais manqués, blocage).</summary>
public sealed record CompteComptoir(long Id, string Identifiant, string NomComplet, string? CodeHash, int Essais, DateTime? BloqueJusqu);

public sealed record VendeurComptoir(string Identifiant, string NomComplet);

public interface ICodesComptoir
{
    Task<CompteComptoir?> TrouverActifAsync(string identifiant, CancellationToken ct = default);
    Task<CompteComptoir?> ParIdAsync(long utilisateurId, CancellationToken ct = default);
    Task EnregistrerEssaiAsync(long utilisateurId, int essais, DateTime? bloqueJusqu, CancellationToken ct = default);
    /// <summary>
    /// Réserve un essai, de façon atomique (essais simultanés comptés un par un) : numéro de l'essai, ou null si le code
    /// est bloqué. L'essai qui atteint le maximum pose le blocage avant même la vérification du code.
    /// </summary>
    Task<int?> ReserverEssaiAsync(long utilisateurId, DateTime maintenant, int essaisMax, DateTime bloqueJusqu, CancellationToken ct = default);
    /// <summary>Enregistre (ou efface avec null) le code haché ; remet les essais à zéro.</summary>
    Task DefinirAsync(long utilisateurId, string? codeHash, CancellationToken ct = default);
    /// <summary>Comptes actifs qui ont un code comptoir.</summary>
    Task<IReadOnlyList<CompteComptoir>> AvecCodeAsync(CancellationToken ct = default);
}

/// <summary>L'employé choisit son propre code ; refusé depuis une connexion par code (il faut le mot de passe).</summary>
public sealed class DefinirCodeComptoir(ICodesComptoir codes, Administration.IHacheurMotDePasse hacheur, IJournal journal)
{
    public async Task ExecuterAsync(long utilisateurId, bool connexionParCode, string? code, CancellationToken ct = default)
    {
        if (connexionParCode)
            throw new RegleMetierException("Reconnectez-vous avec votre mot de passe pour changer votre code comptoir.");
        CodeComptoir.VerifierFormat(code);
        await codes.DefinirAsync(utilisateurId, hacheur.Hacher(code!), ct);
        await journal.EcrireAsync(utilisateurId, "code_comptoir", "utilisateur", utilisateurId,
            new Dictionary<string, object?> { ["action"] = "defini" }, null, ct);
    }
}

/// <summary>Code oublié ou divulgué : un administrateur des utilisateurs l'efface, l'employé en choisit un nouveau.</summary>
public sealed class EffacerCodeComptoir(ICodesComptoir codes, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long cibleId, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminUtilisateurs, "effacer un code comptoir", ct);
        var cible = await codes.ParIdAsync(cibleId, ct) ?? throw new RegleMetierException("Employé introuvable.");
        await codes.DefinirAsync(cibleId, null, ct);
        await journal.EcrireAsync(utilisateurId, "code_comptoir", "utilisateur", cibleId,
            new Dictionary<string, object?> { ["action"] = "efface", ["employe"] = cible.NomComplet }, Permissions.AdminUtilisateurs, ct);
    }
}

/// <summary>
/// Changement d'utilisateur au comptoir : seulement sur un poste dont la caisse est ouverte, avec le droit de vendre.
/// Le jeton obtenu est limité au comptoir ; 5 codes faux bloquent le code quelques minutes.
/// </summary>
public sealed class SeConnecterParCode(ICodesComptoir codes, IVerificateurMotDePasse verificateur, IGenerateurJeton jetons,
    ISessionsCaisse sessions, IPermissionService droits, IComptesLecture comptes, IHorloge horloge, IJournal journal)
{
    public const string Refus = "Code incorrect.";

    public async Task<Session> ExecuterAsync(string? identifiant, string? code, string? poste, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrWhiteSpace(code)) throw new RegleMetierException(Refus);
        if (await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct) is null)
            throw new RegleMetierException("La caisse de ce poste n’est pas ouverte : connectez-vous avec votre mot de passe.");

        var compte = await new VerificationCodeComptoir(codes, verificateur, horloge, journal).VerifierAsync(identifiant, code, poste, ct);
        if (!await droits.APermissionAsync(compte.Id, Permissions.CaisseVendre, ct))
            throw new DroitRefuseException(Permissions.CaisseVendre, "reprendre la caisse");
        await comptes.NoterConnexionAsync(compte.Id, ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet, comptoir: true);
        return new Session(jeton, compte.NomComplet, expire);
    }
}

/// <summary>
/// Vérifie le code comptoir d'un employé (connexion au comptoir, validation sur place d'une action) :
/// 5 codes faux bloquent le code quelques minutes ; le blocage est inscrit au journal.
/// </summary>
public sealed class VerificationCodeComptoir(ICodesComptoir codes, IVerificateurMotDePasse verificateur, IHorloge horloge, IJournal journal)
{
    public async Task<CompteComptoir> VerifierAsync(string? identifiant, string? code, string? poste, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrWhiteSpace(code)) throw new RegleMetierException(SeConnecterParCode.Refus);
        var compte = await codes.TrouverActifAsync(identifiant.Trim(), ct);
        if (compte?.CodeHash is null) throw new RegleMetierException(SeConnecterParCode.Refus);
        var maintenant = horloge.MaintenantUtc;
        // Essai réservé avant la vérification : des requêtes simultanées ne peuvent pas dépasser le nombre d'essais permis
        var essai = await codes.ReserverEssaiAsync(compte.Id, maintenant, CodeComptoir.EssaisMax, maintenant + CodeComptoir.Blocage, ct);
        if (essai is null)
            throw new RegleMetierException(compte.BloqueJusqu is { } b && b > maintenant
                ? $"Code bloqué après {CodeComptoir.EssaisMax} erreurs : réessayez après {b.ToLocalTime():HH:mm} ou connectez-vous avec votre mot de passe."
                : $"Code bloqué après {CodeComptoir.EssaisMax} erreurs : réessayez dans quelques minutes ou connectez-vous avec votre mot de passe.");

        if (!verificateur.Verifier(code.Trim(), compte.CodeHash))
        {
            var bloque = essai >= CodeComptoir.EssaisMax;
            if (bloque)
                await journal.EcrireAsync(compte.Id, "code_comptoir_bloque", "utilisateur", compte.Id,
                    new Dictionary<string, object?> { ["poste"] = poste }, null, ct);
            throw new RegleMetierException(bloque ? $"Code bloqué {CodeComptoir.Blocage.TotalMinutes:0} minutes après {CodeComptoir.EssaisMax} erreurs." : SeConnecterParCode.Refus);
        }

        await codes.EnregistrerEssaiAsync(compte.Id, 0, null, ct);
        return compte;
    }
}

/// <summary>Employés présents pouvant valider une action sur place par leur code (ex. annulation d'une vente ancienne).</summary>
public sealed class ListerValidateurs(ICodesComptoir codes, IPermissionService droits)
{
    public async Task<IReadOnlyList<VendeurComptoir>> ExecuterAsync(string permission, CancellationToken ct = default)
    {
        var liste = new List<VendeurComptoir>();
        foreach (var c in await codes.AvecCodeAsync(ct))
            if (await droits.APermissionAsync(c.Id, permission, ct))
                liste.Add(new VendeurComptoir(c.Identifiant, c.NomComplet));
        return liste.OrderBy(v => v.NomComplet).ToList();
    }
}
