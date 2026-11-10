using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

public sealed record UtilisateurAdmin(
    long Id, string NomComplet, string Identifiant, long RoleId, string Role, bool RoleSysteme,
    bool Actif, DateTime? DerniereConnexion, bool CodeComptoir = false, string? Telephone = null, string? Email = null);

public sealed record RoleAdmin(long Id, string Code, string Nom, bool Systeme, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);
public sealed record PermissionAdmin(string Code, string Module, string Libelle, bool Sensible);
public sealed record EtatDroits(IReadOnlyList<UtilisateurAdmin> Utilisateurs, IReadOnlyList<RoleAdmin> Roles, IReadOnlyList<PermissionAdmin> Permissions);

/// <summary>Droits voulus pour un rôle : remise maximale et liste complète des permissions.</summary>
public sealed record DroitsRole(long RoleId, decimal RemiseMaxPct, IReadOnlyList<string> Permissions);

public interface IUtilisateursAdmin
{
    Task<EtatDroits> LireAsync(CancellationToken ct = default);
    /// <summary>Téléphone et e-mail déjà contrôlés (RegleContact) ; null : aucun.</summary>
    Task<long> CreerAsync(string nomComplet, string identifiant, string hash, long roleId, string? telephone, string? email, CancellationToken ct = default);
    /// <summary>Remplace aussi le téléphone et l'e-mail (null : effacés).</summary>
    Task ModifierAsync(long id, string nomComplet, long roleId, bool actif, string? nouveauHash, string? telephone, string? email, CancellationToken ct = default);
    /// <summary>Remplace les droits des rôles donnés (jamais le rôle système), en une transaction.</summary>
    Task EnregistrerDroitsAsync(IReadOnlyList<DroitsRole> droits, CancellationToken ct = default);
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
                ["mot_de_passe_change"] = hash is not null,
                ["coordonnees_changees"] = tel != cible.Telephone || mail != cible.Email
            }, Permissions.AdminUtilisateurs, ct);
    }
}

/// <summary>Matrice des droits et remises maximales. Le rôle titulaire n'est jamais modifié.</summary>
public sealed class EnregistrerDroits(IUtilisateursAdmin admin, IJournal journal)
{
    public async Task ExecuterAsync(IReadOnlyList<DroitsRole> droits, long auteurId, CancellationToken ct = default)
    {
        var etat = await admin.LireAsync(ct);
        var codes = etat.Permissions.Select(p => p.Code).ToHashSet();
        var aEnregistrer = new List<DroitsRole>();
        foreach (var d in droits)
        {
            var role = etat.Roles.FirstOrDefault(r => r.Id == d.RoleId) ?? throw new RegleMetierException("Rôle inconnu.");
            if (role.Systeme) continue;   // titulaire : toujours tous les droits
            if (d.RemiseMaxPct is < 0 or > 100)
                throw new RegleMetierException($"{role.Nom} : la remise maximale doit être comprise entre 0 et 100 %.");
            if (d.Permissions.FirstOrDefault(p => !codes.Contains(p)) is { } inconnue)
                throw new RegleMetierException($"Permission inconnue : {inconnue}.");
            aEnregistrer.Add(d with { Permissions = d.Permissions.Distinct().ToList() });
        }

        await admin.EnregistrerDroitsAsync(aEnregistrer, ct);
        await journal.EcrireAsync(auteurId, "droits_modifies", "role_permission", null,
            new Dictionary<string, object?>
            {
                ["roles"] = aEnregistrer.Select(d => new
                {
                    role = etat.Roles.First(r => r.Id == d.RoleId).Nom,
                    remise_max = d.RemiseMaxPct,
                    ajoutes = d.Permissions.Except(etat.Roles.First(r => r.Id == d.RoleId).Permissions).ToList(),
                    retires = etat.Roles.First(r => r.Id == d.RoleId).Permissions.Except(d.Permissions).ToList()
                }).ToList()
            }, Permissions.AdminUtilisateurs, ct);
    }
}
