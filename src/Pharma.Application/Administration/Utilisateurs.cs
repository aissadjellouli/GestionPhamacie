using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

public sealed record UtilisateurAdmin(
    long Id, string NomComplet, string Identifiant, long RoleId, string Role, bool RoleSysteme,
    bool Actif, DateTime? DerniereConnexion, bool CodeComptoir = false, string? Telephone = null, string? Email = null,
    bool PeutDonner = false, decimal RemiseMaxPct = 0, bool GereRemisesAuto = false);

public sealed record RoleAdmin(long Id, string Code, string Nom, bool Systeme, IReadOnlyList<string> Permissions);
/// <summary>ParPersonne : droit réglé sur chaque employé par le titulaire (don, remise, remises automatiques), pas par rôle.</summary>
public sealed record PermissionAdmin(string Code, string Module, string Libelle, bool Sensible, bool ParPersonne = false);
/// <summary>Don et remise d'un employé : peut faire un don, remise maximale (% entier), gère les remises automatiques.</summary>
public sealed record DonRemiseEmploye(bool PeutDonner, decimal RemiseMaxPct, bool GereRemisesAuto);
public sealed record EtatDroits(IReadOnlyList<UtilisateurAdmin> Utilisateurs, IReadOnlyList<RoleAdmin> Roles, IReadOnlyList<PermissionAdmin> Permissions);

/// <summary>Droits voulus pour un rôle : liste complète des permissions (hors droits réglés par personne).</summary>
public sealed record DroitsRole(long RoleId, IReadOnlyList<string> Permissions);

public interface IUtilisateursAdmin
{
    Task<EtatDroits> LireAsync(CancellationToken ct = default);
    /// <summary>Téléphone et e-mail déjà contrôlés (RegleContact) ; null : aucun.</summary>
    Task<long> CreerAsync(string nomComplet, string identifiant, string hash, long roleId, string? telephone, string? email, CancellationToken ct = default);
    /// <summary>Remplace aussi le téléphone et l'e-mail (null : effacés).</summary>
    Task ModifierAsync(long id, string nomComplet, long roleId, bool actif, string? nouveauHash, string? telephone, string? email, CancellationToken ct = default);
    /// <summary>Remplace les droits des rôles donnés (jamais le rôle système), en une transaction.</summary>
    Task EnregistrerDroitsAsync(IReadOnlyList<DroitsRole> droits, CancellationToken ct = default);
    /// <summary>Don et remise d'un employé (jamais d'un titulaire : il a toujours tout).</summary>
    Task ReglerDonRemiseAsync(long utilisateurId, DonRemiseEmploye reglage, CancellationToken ct = default);
}

public interface IHacheurMotDePasse
{
    string Hacher(string motDePasse);
}

public sealed class ObtenirDroits(IUtilisateursAdmin admin)
{
    public Task<EtatDroits> ExecuterAsync(CancellationToken ct = default) => admin.LireAsync(ct);
}

public sealed class CreerUtilisateur(IUtilisateursAdmin admin, IHacheurMotDePasse hacheur, IJournal journal, IPermissionService droits)
{
    public async Task<long> ExecuterAsync(string? nomComplet, string? identifiant, string? motDePasse, long roleId,
        long auteurId, string? telephone = null, string? email = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomComplet))
            throw new RegleMetierException("Le nom de l’employé est obligatoire.", "nom_employe");
        var id = RegleCompte.NormaliserIdentifiant(identifiant);
        RegleCompte.VerifierMotDePasse(motDePasse);
        var (tel, mail) = (Contacts.RegleContact.Telephone(telephone), Contacts.RegleContact.Email(email));
        var role = (await admin.LireAsync(ct)).Roles.FirstOrDefault(r => r.Id == roleId)
                   ?? throw new RegleMetierException("Rôle inconnu.");
        // Seul un titulaire crée un autre titulaire (ex. le vrai pharmacien qui remplace le compte de démonstration)
        if (role.Systeme)
            await droits.ExigerTitulaireAsync(auteurId, "créer un compte de pharmacien titulaire", ct);

        var nouveauId = await admin.CreerAsync(nomComplet.Trim(), id, hacheur.Hacher(motDePasse!), roleId, tel, mail, ct);
        await journal.EcrireAsync(auteurId, "utilisateur_cree", "utilisateur", nouveauId,
            new Dictionary<string, object?> { ["identifiant"] = id, ["nom"] = nomComplet.Trim(), ["role"] = role.Nom },
            Permissions.AdminUtilisateurs, ct);
        return nouveauId;
    }
}

public sealed class ModifierUtilisateur(IUtilisateursAdmin admin, IHacheurMotDePasse hacheur, IJournal journal, IPermissionService droits)
{
    /// <param name="nouveauMotDePasse">Vide : le mot de passe ne change pas.</param>
    public async Task ExecuterAsync(long id, string? nomComplet, long roleId, bool actif, string? nouveauMotDePasse,
        long auteurId, string? telephone = null, string? email = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(nomComplet))
            throw new RegleMetierException("Le nom de l’employé est obligatoire.", "nom_employe");
        var (tel, mail) = (Contacts.RegleContact.Telephone(telephone), Contacts.RegleContact.Email(email));
        var etat = await admin.LireAsync(ct);
        var cible = etat.Utilisateurs.FirstOrDefault(u => u.Id == id)
                    ?? throw new RegleMetierException("Utilisateur introuvable.");
        var role = etat.Roles.FirstOrDefault(r => r.Id == roleId) ?? throw new RegleMetierException("Rôle inconnu.");
        // Compte d'un titulaire, ou rôle titulaire donné : réservé à un titulaire.
        // La base garde toujours au moins un titulaire actif (déclencheur trg_garder_un_titulaire).
        if (cible.RoleSysteme)
            await droits.ExigerTitulaireAsync(auteurId, "modifier le compte d'un pharmacien titulaire", ct);
        if (role.Systeme && !cible.RoleSysteme)
            await droits.ExigerTitulaireAsync(auteurId, "donner le rôle de pharmacien titulaire", ct);

        string? hash = null;
        if (!string.IsNullOrEmpty(nouveauMotDePasse))
        {
            RegleCompte.VerifierMotDePasse(nouveauMotDePasse);
            hash = hacheur.Hacher(nouveauMotDePasse);
        }

        await admin.ModifierAsync(id, nomComplet.Trim(), roleId, actif, hash, tel, mail, ct);
        await journal.EcrireAsync(auteurId, "utilisateur_modifie", "utilisateur", id,
            new Dictionary<string, object?>
            {
                ["identifiant"] = cible.Identifiant, ["role"] = role.Nom, ["actif"] = actif,
                // Journal des modifications : valeurs avant le changement
                ["role_avant"] = cible.Role, ["actif_avant"] = cible.Actif,
                ["nom"] = cible.NomComplet == nomComplet.Trim() ? null : new { avant = cible.NomComplet, apres = nomComplet.Trim() },
                ["mot_de_passe_change"] = hash is not null,
                ["coordonnees_changees"] = tel != cible.Telephone || mail != cible.Email
            }, Permissions.AdminUtilisateurs, ct);
    }
}

/// <summary>
/// Don et remise d'un employé, décidés par le pharmacien titulaire seul : peut faire un don, remise maximale (entier de 0 à 100 %),
/// gère les remises automatiques. Le titulaire a toujours tout, sans réglage. Inscrit au journal (avant, après).
/// </summary>
public sealed class ReglerDonRemise(IUtilisateursAdmin admin, IJournal journal, IPermissionService droits)
{
    public async Task ExecuterAsync(long utilisateurId, DonRemiseEmploye reglage, long auteurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(auteurId, "régler le don et la remise d’un employé", ct);
        if (reglage.RemiseMaxPct is < 0 or > 100 || reglage.RemiseMaxPct != Math.Truncate(reglage.RemiseMaxPct))
            throw new RegleMetierException("La remise maximale est un nombre entier de 0 à 100 %.", "remise_max");
        var cible = (await admin.LireAsync(ct)).Utilisateurs.FirstOrDefault(u => u.Id == utilisateurId)
                    ?? throw new RegleMetierException("Utilisateur introuvable.");
        if (cible.RoleSysteme)
            throw new RegleMetierException("Le pharmacien titulaire peut toujours faire un don et accorder une remise jusqu’à 100 %.");
        var avant = new DonRemiseEmploye(cible.PeutDonner, cible.RemiseMaxPct, cible.GereRemisesAuto);
        if (avant == reglage) return;
        await admin.ReglerDonRemiseAsync(utilisateurId, reglage, ct);
        await journal.EcrireAsync(auteurId, "don_remise_employe", "utilisateur", utilisateurId,
            new Dictionary<string, object?>
            {
                ["identifiant"] = cible.Identifiant,
                ["avant"] = new { peut_donner = avant.PeutDonner, remise_max = avant.RemiseMaxPct, remises_auto = avant.GereRemisesAuto },
                ["apres"] = new { peut_donner = reglage.PeutDonner, remise_max = reglage.RemiseMaxPct, remises_auto = reglage.GereRemisesAuto }
            }, Permissions.AdminUtilisateurs, ct);
    }
}

/// <summary>Matrice des droits par rôle (hors droits réglés par personne). Le rôle titulaire n'est jamais modifié.</summary>
public sealed class EnregistrerDroits(IUtilisateursAdmin admin, IJournal journal)
{
    public async Task ExecuterAsync(IReadOnlyList<DroitsRole> droits, long auteurId, CancellationToken ct = default)
    {
        var etat = await admin.LireAsync(ct);
        var codes = etat.Permissions.Where(p => !p.ParPersonne).Select(p => p.Code).ToHashSet();
        var aEnregistrer = new List<DroitsRole>();
        foreach (var d in droits)
        {
            var role = etat.Roles.FirstOrDefault(r => r.Id == d.RoleId) ?? throw new RegleMetierException("Rôle inconnu.");
            if (role.Systeme) continue;   // titulaire : toujours tous les droits
            if (d.Permissions.FirstOrDefault(p => !codes.Contains(p)) is { } inconnue)
                throw new RegleMetierException(etat.Permissions.Any(p => p.Code == inconnue)
                    ? $"« {inconnue} » se règle sur chaque employé (Utilisateurs), pas par rôle."
                    : $"Permission inconnue : {inconnue}.");
            aEnregistrer.Add(d with { Permissions = d.Permissions.Distinct().ToList() });
        }

        await admin.EnregistrerDroitsAsync(aEnregistrer, ct);
        await journal.EcrireAsync(auteurId, "droits_modifies", "role_permission", null,
            new Dictionary<string, object?>
            {
                ["roles"] = aEnregistrer.Select(d => new
                {
                    role = etat.Roles.First(r => r.Id == d.RoleId).Nom,
                    ajoutes = d.Permissions.Except(etat.Roles.First(r => r.Id == d.RoleId).Permissions).ToList(),
                    retires = etat.Roles.First(r => r.Id == d.RoleId).Permissions.Except(d.Permissions).ToList()
                }).ToList()
            }, Permissions.AdminUtilisateurs, ct);
    }
}
