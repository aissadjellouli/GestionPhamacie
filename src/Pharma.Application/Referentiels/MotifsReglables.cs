using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Securite;

namespace Pharma.Application.Referentiels;

// ---- Port -------------------------------------------------------------------

/// <summary>Table motif_reglable (094_motifs_reglables.sql) : jamais de suppression.</summary>
public interface IMotifsReglables
{
    /// <summary>Motifs d'une liste (actifs et désactivés), ou de toutes les listes (liste null), dans l'ordre d'affichage.</summary>
    Task<IReadOnlyList<MotifReglable>> ListerAsync(string? liste, CancellationToken ct = default);
    Task<long> CreerAsync(string liste, string code, string libelle, CancellationToken ct = default);
    Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default);
}

// ---- Cas d'usage --------------------------------------------------------------

/// <summary>Motifs actifs d'une liste, proposés à la saisie (caisse, trésorerie, stock, retours) : tout employé connecté.</summary>
public sealed class MotifsActifs(IMotifsReglables motifs)
{
    public async Task<IReadOnlyList<MotifReglable>> ExecuterAsync(string? liste, CancellationToken ct = default) =>
        (await motifs.ListerAsync(RegleMotifs.ExigerListe(liste), ct)).Where(m => m.Actif).ToList();

    /// <summary>Choix contrôlé : motif actif de la liste, « Autre » avec texte (RegleMotifs.Choisir).</summary>
    public async Task<(string Code, string Libelle, string? Texte)> ChoisirAsync(string liste, string? code, string? texte, string quoi,
        CancellationToken ct = default) =>
        RegleMotifs.Choisir(await ExecuterAsync(liste, ct), code, texte, quoi);
}

/// <summary>Tableau de gestion (Administration › Motifs et catégories) : filtré et paginé ; droit admin.parametres.</summary>
public sealed class ListerMotifsReglables(IMotifsReglables motifs, IPermissionService droits)
{
    public async Task<Page<MotifReglable>> ExecuterAsync(string? liste, string? etat, string? recherche, DemandePage page, long utilisateurId,
        CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler les motifs et catégories", ct);
        var l = string.IsNullOrWhiteSpace(liste) ? null : RegleMotifs.ExigerListe(liste);
        return PageEnMemoire.Paginer(Filtrer(await motifs.ListerAsync(l, ct), etat, recherche), page);
    }

    public static IReadOnlyList<MotifReglable> Filtrer(IEnumerable<MotifReglable> liste, string? etat, string? recherche) => liste
        .Where(m => etat switch { "actif" => m.Actif, "inactif" => !m.Actif, _ => true })
        .Where(m => TexteRecherche.Contient(recherche, m.Libelle, m.Code))
        .OrderBy(m => RegleMotifs.Listes.Select(x => x.Code).ToList().IndexOf(m.Liste)).ThenBy(m => m.Ordre).ThenBy(m => m.Libelle, StringComparer.CurrentCulture)
        .ToList();
}

/// <summary>Ajout (tiroir, attente, charges) ou modification du libellé et de l'activation ; journalisé (droit admin.parametres).</summary>
public sealed class EnregistrerMotifReglable(IMotifsReglables motifs, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(long? id, string? liste, string? libelle, bool actif, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.AdminParametres, "régler les motifs et catégories", ct);
        var l = RegleMotifs.ExigerListe(liste);
        var lib = RegleMotifs.VerifierLibelle(libelle);
        var existants = await motifs.ListerAsync(l, ct);
        if (existants.Any(m => m.Id != id && string.Equals(m.Libelle, lib, StringComparison.CurrentCultureIgnoreCase)))
            throw new RegleMetierException($"« {lib} » existe déjà dans cette liste (réactivez-le s’il est désactivé).", "libelle");

        string code;
        if (id is null)
        {
            if (!RegleMotifs.AjoutPermis(l))
                throw new RegleMetierException("Cette liste ne reçoit pas de nouveau motif : seuls les libellés et l’activation se règlent.", "liste");
            code = RegleMotifs.CodeDepuisLibelle(lib, existants.Select(m => m.Code));
            id = await motifs.CreerAsync(l, code, lib, ct);
            actif = true;
        }
        else
        {
            var m = existants.FirstOrDefault(x => x.Id == id) ?? throw new RegleMetierException("Motif introuvable.");
            RegleMotifs.VerifierModification(m, actif);
            code = m.Code;
            if (!await motifs.ModifierAsync(id.Value, lib, actif, ct)) throw new RegleMetierException("Motif introuvable.");
        }

        await journal.EcrireAsync(utilisateurId, "motif_reglable", "motif_reglable", id,
            new Dictionary<string, object?> { ["liste"] = l, ["code"] = code, ["libelle"] = lib, ["actif"] = actif },
            Permissions.AdminParametres, ct);
        return id.Value;
    }
}
