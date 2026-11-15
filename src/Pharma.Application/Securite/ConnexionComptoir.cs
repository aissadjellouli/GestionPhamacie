using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Securite;

/// <summary>Compte vu pour une connexion par code comptoir (code haché, essais manqués, blocage).</summary>
/// <summary>DoitChangerMotDePasse : mot de passe provisoire pas encore changé, le code comptoir ne vaut rien.</summary>
public sealed record CompteComptoir(long Id, string Identifiant, string NomComplet, string? CodeHash, int Essais, DateTime? BloqueJusqu,
    bool DoitChangerMotDePasse = false);

public sealed record VendeurComptoir(string Identifiant, string NomComplet, long Id = 0);

public interface ICodesComptoir
{
    Task<CompteComptoir?> TrouverActifAsync(string identifiant, CancellationToken ct = default);
    Task<CompteComptoir?> ParIdAsync(long utilisateurId, CancellationToken ct = default);
    Task EnregistrerEssaiAsync(long utilisateurId, int essais, DateTime? bloqueJusqu, CancellationToken ct = default);
    /// <summary>
    /// Réserve un essai, de façon atomique (essais simultanés comptés un par un) : numéro de l'essai, ou null si le code
    /// est bloqué. L'essai qui atteint le maximum pose le blocage avant même la vérification du code.
    /// </summary>
    /// Blocage progressif (CodeComptoir.DureeBlocage) : le compteur n'est remis à zéro que par un bon code ou un nouveau code.
    Task<int?> ReserverEssaiAsync(long utilisateurId, DateTime maintenant, CancellationToken ct = default);
    /// <summary>Compte actif par son numéro (référence d'un valideur, sans son identifiant de connexion).</summary>
    Task<CompteComptoir?> TrouverActifParIdAsync(long utilisateurId, CancellationToken ct = default);
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
    ISessionsCaisse sessions, IPermissionService droits, IComptesLecture comptes, IHorloge horloge, IJournal journal,
    Surveillance.Surveiller? surveillance = null, Notifications.IAlertesTitulaire? alertes = null)
{
    public const string Refus = "Code incorrect.";
    public const string MotDePasseProvisoire = "Mot de passe provisoire : connectez-vous d’abord avec votre mot de passe et choisissez-en un personnel.";

    public async Task<Session> ExecuterAsync(string? identifiant, string? code, string? poste, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrWhiteSpace(code)) throw new RegleMetierException(Refus);
        if (await sessions.SessionDuPosteAsync(PosteCaisse.Exiger(poste), ct) is null)
            throw new RegleMetierException("La caisse de ce poste n’est pas ouverte : connectez-vous avec votre mot de passe.");

        var compte = await new VerificationCodeComptoir(codes, verificateur, horloge, journal, alertes).VerifierAsync(identifiant, code, poste, ct);
        if (!await droits.APermissionAsync(compte.Id, Permissions.CaisseVendre, ct))
            throw new DroitRefuseException(Permissions.CaisseVendre, "reprendre la caisse");
        await comptes.NoterConnexionAsync(compte.Id, poste?.Trim(), "code", ct);
        // Surveillance : même employé actif sur deux postes à la fois
        if (surveillance is not null) await surveillance.ExecuterAsync(ct);
        var (jeton, expire) = jetons.Creer(compte.Id, compte.NomComplet, comptoir: true);
        return new Session(jeton, compte.NomComplet, expire);
    }
}

/// <summary>
/// Vérifie le code comptoir d'un employé (connexion au comptoir, validation sur place d'une action) :
/// 5 codes faux bloquent le code quelques minutes ; le blocage est inscrit au journal.
/// </summary>
public sealed class VerificationCodeComptoir(ICodesComptoir codes, IVerificateurMotDePasse verificateur, IHorloge horloge, IJournal journal,
    Notifications.IAlertesTitulaire? alertes = null, EssaisCodeDemandeur? demandeurs = null)
{
    /// <summary>Référence d'un valideur proposé au poste (« #numéro ») : son identifiant de connexion n'est pas divulgué.</summary>
    public static string Reference(long utilisateurId) => "#" + utilisateurId;

    private async Task<CompteComptoir?> TrouverAsync(string identifiant, CancellationToken ct) =>
        identifiant.StartsWith('#') && long.TryParse(identifiant.AsSpan(1), out var id)
            ? await codes.TrouverActifParIdAsync(id, ct)
            : await codes.TrouverActifAsync(identifiant, ct);

    private static string Minutes(TimeSpan d) => d.TotalHours >= 1 ? $"{d.TotalHours:0.#} h" : $"{d.TotalMinutes:0} minutes";

    /// <param name="demandeurId">
    /// Employé connecté qui fait valider une action (autorisation sur place) ; null pour la connexion par code (écran de connexion).
    /// Avec un demandeur, les codes faux sont comptés pour LUI (revue 2026-11-15, I2) : au-delà de EssaisCodeDemandeur.EssaisMax en
    /// 15 minutes, c'est lui qui est bloqué ; le valideur n'est bloqué que si plusieurs demandeurs différents se trompent sur son code.
    /// </param>
    public async Task<CompteComptoir> VerifierAsync(string? identifiant, string? code, string? poste, CancellationToken ct = default,
        long? demandeurId = null)
    {
        if (string.IsNullOrWhiteSpace(identifiant) || string.IsNullOrWhiteSpace(code)) throw new RegleMetierException(SeConnecterParCode.Refus);
        var compte = await TrouverAsync(identifiant.Trim(), ct);
        if (compte?.CodeHash is null) throw new RegleMetierException(SeConnecterParCode.Refus);
        var maintenant = horloge.MaintenantUtc;
        if (demandeurId is { } d && demandeurs is not null)
            return await VerifierPourDemandeurAsync(compte, code, poste, d, maintenant, ct);
        // Essai réservé avant la vérification : des requêtes simultanées ne peuvent pas dépasser le nombre d'essais permis
        var essai = await codes.ReserverEssaiAsync(compte.Id, maintenant, ct);
        if (essai is null)
            throw new RegleMetierException(compte.BloqueJusqu is { } b && b > maintenant
                ? $"Code bloqué après des erreurs répétées : réessayez après {b.ToLocalTime():HH:mm} ou connectez-vous avec votre mot de passe."
                : "Code bloqué après des erreurs répétées : réessayez plus tard ou connectez-vous avec votre mot de passe.");

        if (!verificateur.Verifier(code.Trim(), compte.CodeHash))
            throw new RegleMetierException(await EchecValideurAsync(compte, essai.Value, poste, ct));

        await codes.EnregistrerEssaiAsync(compte.Id, 0, null, ct);
        // Mot de passe provisoire pas encore changé : aucun droit, ni par la connexion par code ni pour autoriser un collègue
        if (compte.DoitChangerMotDePasse) throw new RegleMetierException(SeConnecterParCode.MotDePasseProvisoire);
        return compte;
    }

    /// <summary>Code faux compté au valideur : blocage progressif, journal et alerte au titulaire ; renvoie le message à montrer.</summary>
    private async Task<string> EchecValideurAsync(CompteComptoir compte, int essai, string? poste, CancellationToken ct)
    {
        var duree = CodeComptoir.DureeBlocage(essai);
        if (duree is { } d)
        {
            await journal.EcrireAsync(compte.Id, "code_comptoir_bloque", "utilisateur", compte.Id,
                new Dictionary<string, object?> { ["poste"] = poste, ["essais"] = essai, ["minutes"] = (int)d.TotalMinutes }, null, ct);
            // Au-delà du seuil : chaque blocage est signalé au titulaire (mis en file, jamais bloquant)
            if (essai >= CodeComptoir.SeuilAlerte && alertes is not null)
                await alertes.SurveillanceAsync(
                    $"Code comptoir de {compte.NomComplet} bloqué {Minutes(d)} après {essai} codes faux d’affilée (poste {poste ?? "inconnu"}).",
                    urgent: true, ct);
        }
        return duree is { } bloque ? $"Code bloqué {Minutes(bloque)} après {essai} erreurs." : SeConnecterParCode.Refus;
    }

    /// <summary>Autorisation sur place : essais comptés au demandeur (voir VerifierAsync).</summary>
    private async Task<CompteComptoir> VerifierPourDemandeurAsync(CompteComptoir compte, string code, string? poste, long demandeurId,
        DateTime maintenant, CancellationToken ct)
    {
        // Valideur déjà bloqué (connexion par code ou plusieurs demandeurs) : refusé sans rien compter de plus
        if (compte.BloqueJusqu is { } bv && bv > maintenant)
            throw new RegleMetierException($"Code bloqué après des erreurs répétées : réessayez après {bv.ToLocalTime():HH:mm}.");
        var r = demandeurs!.Reserver(demandeurId, maintenant);
        if (r.Sature) throw new RegleMetierException("Trop de demandes d’autorisation par code en cours : réessayez dans quelques minutes.");
        if (r.Essai is null)
            throw new RegleMetierException(
                $"Autorisation par code bloquée pour vous après {EssaisCodeDemandeur.EssaisMax} codes faux : réessayez après {r.BloqueJusqu!.Value.ToLocalTime():HH:mm}.");

        if (!verificateur.Verifier(code.Trim(), compte.CodeHash!))
        {
            if (r.VientDeBloquer)
            {
                await journal.EcrireAsync(demandeurId, "code_comptoir_demandeur_bloque", "utilisateur", demandeurId,
                    new Dictionary<string, object?> { ["poste"] = poste, ["essais"] = r.Essai, ["valideur"] = compte.NomComplet,
                        ["minutes"] = (int)EssaisCodeDemandeur.Blocage.TotalMinutes }, null, ct);
                if (alertes is not null)
                    await alertes.SurveillanceAsync(
                        $"Autorisation par code bloquée {Minutes(EssaisCodeDemandeur.Blocage)} pour l’employé n° {demandeurId} après {r.Essai} codes faux "
                        + $"(code de {compte.NomComplet}, poste {poste ?? "inconnu"}).", urgent: true, ct);
            }
            // Plusieurs demandeurs différents se trompent sur ce code : le blocage progressif du valideur s'applique
            if (demandeurs.NoterEchecValideur(compte.Id, demandeurId, maintenant) >= 2
                && await codes.ReserverEssaiAsync(compte.Id, maintenant, ct) is { } essai)
            {
                var message = await EchecValideurAsync(compte, essai, poste, ct);
                if (message != SeConnecterParCode.Refus) throw new RegleMetierException(message);
            }
            throw new RegleMetierException(r.VientDeBloquer
                ? $"Autorisation par code bloquée pour vous {Minutes(EssaisCodeDemandeur.Blocage)} après {r.Essai} codes faux."
                : SeConnecterParCode.Refus);
        }

        demandeurs.NoterSucces(demandeurId);
        if (compte.Essais > 0) await codes.EnregistrerEssaiAsync(compte.Id, 0, null, ct);
        if (compte.DoitChangerMotDePasse) throw new RegleMetierException(SeConnecterParCode.MotDePasseProvisoire);
        return compte;
    }
}

/// <summary>Employés présents pouvant valider une action sur place par leur code (ex. annulation d'une vente ancienne).</summary>
public sealed class ListerValidateurs(ICodesComptoir codes, IPermissionService droits)
{
    /// <param name="sauf">Employé qui demande l'autorisation : jamais proposé (on ne s'autorise pas soi-même).</param>
    /// <param name="remisePct">Remise refusée à valider : seuls les employés dont le maximum de remise la couvre sont proposés.</param>
    public async Task<IReadOnlyList<VendeurComptoir>> ExecuterAsync(string permission, CancellationToken ct = default, long? sauf = null,
        decimal? remisePct = null)
    {
        var liste = new List<VendeurComptoir>();
        foreach (var c in (await codes.AvecCodeAsync(ct)).Where(c => c.Id != sauf))
            if ((permission == PermissionServiceExtensions.MarqueTitulaire ? await droits.EstTitulaireAsync(c.Id, ct)
                    : await droits.APermissionAsync(c.Id, CodeComptoir.DroitDuValideur(permission), ct))
                && (remisePct is not { } pct || await droits.RemiseMaxAsync(c.Id, ct) >= pct))
                liste.Add(new VendeurComptoir(c.Identifiant, c.NomComplet, c.Id));
        return liste.OrderBy(v => v.NomComplet).ToList();
    }

    /// <summary>
    /// « Faire autoriser » (GET /api/auth/valideurs) : seulement pour un droit de la liste blanche (CodeComptoir.AutorisablesParCollegue),
    /// jamais pour le rôle de titulaire ou un droit d'administration ; liste vide sinon.
    /// </summary>
    public async Task<IReadOnlyList<VendeurComptoir>> AutorisablesAsync(string? permission, long sauf, decimal? remisePct, CancellationToken ct = default)
    {
        if (!CodeComptoir.AutorisableParCollegue(permission)) return [];
        return await ExecuterAsync(permission!, ct, sauf, remisePct);
    }
}
