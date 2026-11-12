using Pharma.Application.Commun;
using Pharma.Application.Mobile;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Achats;

/// <summary>Réception mise en attente : saisie de l'écran Réceptions (JSON) gardée sur le serveur.</summary>
public sealed record ReceptionEnAttente(long Id, string Libelle, string Mode, int Lignes, long EmployeId, string Employe, string Poste, DateTime CreeLe);

/// <summary>Contenu repris : la saisie et les scans du mobile qu'elle contient.</summary>
public sealed record ReceptionReprise(string Contenu, string Mode, long EmployeId);

public interface IReceptionsEnAttente
{
    Task<IReadOnlyList<ReceptionEnAttente>> ListerAsync(CancellationToken ct = default);
    Task<long> EnregistrerAsync(string poste, long utilisateurId, string libelle, string mode, int lignes, string contenu, IReadOnlyList<long> scans,
        CancellationToken ct = default);
    /// <summary>Lecture sans retrait (contrôle avant reprise) ; null si elle n'existe plus.</summary>
    Task<ReceptionEnAttente?> TrouverAsync(long id, CancellationToken ct = default);
    /// <summary>Contenu retiré de l'attente en une seule requête ; null si un collègue l'a déjà reprise.</summary>
    Task<ReceptionReprise?> ReprendreAsync(long id, CancellationToken ct = default);
    /// <summary>Abandon : libellé et scans du mobile de la réception supprimée, ou null si elle n'existe plus.</summary>
    Task<(string Libelle, IReadOnlyList<long> Scans)?> SupprimerAsync(long id, CancellationToken ct = default);
}

/// <summary>
/// Mettre de côté, reprendre ou abandonner une réception de livraison (droit achats.receptionner), depuis n'importe quel
/// poste. Réception à l'aveugle (comptage, recompte) : reprise ou abandon par l'employé qui a compté, ou par qui décide des
/// écarts (achats.valider_ecart, qui voit déjà facturé et compté) — jamais par la personne qui saisira la facture sans voir le compté.
/// Ainsi un comptage n'est jamais bloqué si son auteur ne revient pas.
/// </summary>
public sealed class GererReceptionsEnAttente(IReceptionsEnAttente attente, IMobile mobile, IJournal journal, IPermissionService droits)
{
    public const int Max = 20;
    public const int TailleMax = 500_000;
    public static readonly IReadOnlyList<string> Modes = ["reception", "facture", "comptage", "recompte"];

    public Task<IReadOnlyList<ReceptionEnAttente>> ListerAsync(CancellationToken ct = default) => attente.ListerAsync(ct);

    public async Task<long> MettreEnAttenteAsync(string? poste, long utilisateurId, string? libelle, string? mode, int lignes, string? contenu,
        IReadOnlyList<long>? scans, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(contenu)) throw new RegleMetierException("Réception vide : rien à mettre en attente.");
        if (contenu.Length > TailleMax) throw new RegleMetierException("Réception trop volumineuse pour être mise en attente.");
        var m = mode is not null && Modes.Contains(mode) ? mode : "reception";
        if ((await attente.ListerAsync(ct)).Count >= Max)
            throw new RegleMetierException($"Déjà {Max} réceptions en attente : reprenez-en ou abandonnez-en une.");
        var nom = string.IsNullOrWhiteSpace(libelle) ? "Réception en attente" : libelle.Trim();
        return await attente.EnregistrerAsync(poste?.Trim() ?? "", utilisateurId, nom.Length > 80 ? nom[..80] : nom, m, Math.Max(lignes, 0), contenu,
            scans?.Distinct().ToList() ?? [], ct);
    }

    public async Task<ReceptionReprise> ReprendreAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        var r = await attente.TrouverAsync(id, ct) ?? throw new RegleMetierException("Cette réception en attente n’existe plus (déjà reprise par un collègue ?).");
        if (!await PeutToucherAsync(r, utilisateurId, ct))
            throw new RegleMetierException($"Comptage à l’aveugle : seul {r.Employe} ou la personne qui décide des écarts de réception peut le reprendre (la facture se saisit sans voir les quantités comptées).");
        return await attente.ReprendreAsync(id, ct)
            ?? throw new RegleMetierException("Cette réception en attente n’existe plus (déjà reprise par un collègue ?).");
    }

    /// <summary>Comptage à l'aveugle : son auteur, ou qui décide des écarts ; les autres modes : tout employé qui réceptionne.</summary>
    private async Task<bool> PeutToucherAsync(ReceptionEnAttente r, long utilisateurId, CancellationToken ct) =>
        r.Mode is not ("comptage" or "recompte") || r.EmployeId == utilisateurId
        || await droits.APermissionAsync(utilisateurId, Permissions.AchatsValiderEcart, ct);

    /// <summary>Abandon : les scans du mobile reviennent en attente d'import ; inscrit au journal (qui, quand, quel BL).</summary>
    public async Task SupprimerAsync(long id, long utilisateurId, CancellationToken ct = default)
    {
        if (utilisateurId <= 0) throw new RegleMetierException("Abandon d’une réception en attente : employé inconnu.");
        var r = await attente.TrouverAsync(id, ct) ?? throw new RegleMetierException("Cette réception en attente n’existe plus.");
        if (!await PeutToucherAsync(r, utilisateurId, ct))
            throw new RegleMetierException($"Comptage à l’aveugle : seul {r.Employe} ou la personne qui décide des écarts de réception peut l’abandonner.");
        var (libelle, scans) = await attente.SupprimerAsync(id, ct) ?? throw new RegleMetierException("Cette réception en attente n’existe plus.");
        var remis = scans.Count > 0 ? await mobile.RemettreEnAttenteAsync(scans, ct) : 0;
        await journal.EcrireAsync(utilisateurId, "reception_attente_abandonnee", "reception_attente", id,
            new Dictionary<string, object?> { ["libelle"] = libelle, ["mode"] = r.Mode, ["lignes"] = r.Lignes, ["mise_en_attente_par"] = r.Employe,
                ["scans_mobile_remis"] = remis }, null, ct);
    }
}
