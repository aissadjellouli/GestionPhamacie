using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Reprise;
using Pharma.Domain.Securite;

namespace Pharma.Application.Reprise;

// ---- Profils de reprise par logiciel concurrent (script 114) ------------------------------------------

/// <summary>Filtre de la liste des profils : texte cherché dans le logiciel et la version, statut (vide : tous).</summary>
public sealed record FiltreProfilsReprise(string? Recherche = null, string? Statut = null);

public interface IProfilsReprise
{
    Task<Page<ProfilReprise>> ListerAsync(FiltreProfilsReprise filtre, DemandePage page, CancellationToken ct = default);
    Task<ProfilReprise?> LireAsync(long id, CancellationToken ct = default);
    /// <summary>Id 0 : création ; sinon mise à jour. Même logiciel et même version qu'un autre profil : refusé.</summary>
    Task<long> EnregistrerAsync(ProfilReprise profil, string auteur, CancellationToken ct = default);
}

public sealed class ListerProfilsReprise(IProfilsReprise profils, SuperAdmin.SuperAdminCourant superAdmin)
{
    public async Task<Page<ProfilReprise>> ExecuterAsync(FiltreProfilsReprise filtre, DemandePage page, CancellationToken ct = default)
    {
        superAdmin.Exiger("préparer la reprise de données");
        var statut = string.IsNullOrWhiteSpace(filtre.Statut) ? null : filtre.Statut.Trim();
        if (statut is not null && !ProfilsReprise.Statuts.Contains(statut)) throw new RegleMetierException("Statut inconnu.", "statut");
        var recherche = string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim();
        if (recherche is { Length: > 100 }) throw new RegleMetierException("Recherche : 100 caractères au plus.", "recherche");
        return await profils.ListerAsync(new FiltreProfilsReprise(recherche, statut), page, ct);
    }

    public async Task<ProfilReprise?> LireAsync(long id, CancellationToken ct = default)
    {
        superAdmin.Exiger("préparer la reprise de données");
        return await profils.LireAsync(id, ct);
    }
}

/// <summary>Création ou modification d'un profil (correspondance établie à partir d'un fichier exemple) ; journalisée.</summary>
public sealed class EnregistrerProfilReprise(IProfilsReprise profils, SuperAdmin.SuperAdminCourant superAdmin, IJournal journal)
{
    public async Task<long> ExecuterAsync(ProfilReprise profil, long utilisateurId, CancellationToken ct = default)
    {
        superAdmin.Exiger("enregistrer un profil de reprise");
        var p = ProfilsReprise.Valider(profil);
        if (p.Id > 0 && await profils.LireAsync(p.Id, ct) is null) throw new RegleMetierException("Profil de reprise introuvable.");
        var id = await profils.EnregistrerAsync(p, superAdmin.Auteur, ct);
        var details = superAdmin.Trace();
        details["logiciel"] = p.Logiciel;
        details["version"] = p.Version;
        details["statut"] = p.Statut;
        details["creation"] = p.Id == 0;
        details["cibles"] = p.Correspondances.Select(c => ExtraireReprise.Libelle(c.Cible)).ToList();
        await journal.EcrireAsync(utilisateurId, "reprise_profil", "import.profil_reprise", id, details, null, ct);
        return id;
    }
}

// ---- Historique des ventes Chifa repris (lecture seule, script 114) -------------------------------------

/// <summary>Historique des organismes repris (121) : Organisme CHIFA (CNAS ou CASNOS non précisé), CNAS, CASNOS ou MDN ;
/// PartChifa : part de l'organisme (Chifa ou MDN) ; NumAssure / NomAssure : assuré ou affilié MDN.</summary>
public sealed record LigneHistoriqueChifa(long Id, long SessionId, string Logiciel, string NumFacture, DateOnly DateFacture,
    string? NumAssure, string? NomAssure, decimal? MontantTotal, decimal? PartChifa, decimal? PartAssure, string Organisme = "CHIFA");

/// <summary>Texte cherché dans le n° de facture, le n° et le nom de l'assuré ; période de facture (bornes incluses) ;
/// Organisme : CHIFA (toutes les factures Chifa : CHIFA, CNAS, CASNOS), CNAS, CASNOS, MDN ; vide : tous.</summary>
public sealed record FiltreHistoriqueChifa(string? Recherche = null, DateOnly? Du = null, DateOnly? Au = null, string? Organisme = null)
{
    public static readonly string[] Organismes = ["CHIFA", "CNAS", "CASNOS", "MDN"];
}

public interface IHistoriqueChifa
{
    Task<Page<LigneHistoriqueChifa>> ListerAsync(FiltreHistoriqueChifa filtre, DemandePage page, CancellationToken ct = default);
}

/// <summary>Consultation réservée au titulaire et au droit « bordereaux Chifa » (chifa.bordereaux).</summary>
public sealed class ConsulterHistoriqueChifa(IHistoriqueChifa historique, IPermissionService droits)
{
    public async Task<Page<LigneHistoriqueChifa>> ExecuterAsync(FiltreHistoriqueChifa filtre, DemandePage page, long utilisateurId, CancellationToken ct = default)
    {
        if (!await droits.EstTitulaireAsync(utilisateurId, ct))
            await droits.ExigerAsync(utilisateurId, Permissions.ChifaBordereaux, "consulter l’historique Chifa repris", ct);
        var recherche = string.IsNullOrWhiteSpace(filtre.Recherche) ? null : filtre.Recherche.Trim();
        if (recherche is { Length: > 100 }) throw new RegleMetierException("Recherche : 100 caractères au plus.", "recherche");
        if (filtre is { Du: { } du, Au: { } au } && du > au) throw new RegleMetierException("La date de début doit précéder la date de fin.", "du");
        var organisme = string.IsNullOrWhiteSpace(filtre.Organisme) ? null : filtre.Organisme.Trim().ToUpperInvariant();
        if (organisme is not null && !FiltreHistoriqueChifa.Organismes.Contains(organisme))
            throw new RegleMetierException("Organisme : CHIFA, CNAS, CASNOS ou MDN.", "organisme");
        return await historique.ListerAsync(filtre with { Recherche = recherche, Organisme = organisme }, page, ct);
    }
}
