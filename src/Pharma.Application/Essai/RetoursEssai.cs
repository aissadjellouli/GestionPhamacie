using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Essai;

/// <summary>Remarque d'un testeur : écran concerné, genre (probleme, suggestion, question) et texte libre.</summary>
public sealed record RetourEssai(DateTime Date, string Employe, string Ecran, string Genre, string Texte);

public interface IRetoursEssai
{
    /// <summary>Retours inscrits au journal (action « retour_essai »), du plus récent au plus ancien ; utilisateurId null : tous.</summary>
    Task<IReadOnlyList<RetourEssai>> ListerAsync(long? utilisateurId, int limite, CancellationToken ct = default);
}

/// <summary>
/// Retours du pharmacien pendant l'essai, inscrits au journal des actions (aucune table dédiée) : chacun voit les siens, le titulaire
/// les voit tous et peut les copier pour les transmettre.
/// </summary>
public sealed class RetoursEssai(IRetoursEssai retours, IJournal journal, IPermissionService droits)
{
    public static readonly string[] Genres = ["probleme", "suggestion", "question"];

    public async Task EnregistrerAsync(string? ecran, string? genre, string? texte, long utilisateurId, CancellationToken ct = default)
    {
        var t = texte?.Trim() ?? "";
        if (t.Length == 0) throw new RegleMetierException("Décrivez votre remarque.", "texte_retour");
        if (t.Length > 4000) throw new RegleMetierException("Remarque trop longue (4 000 caractères au plus).", "texte_retour");
        var g = genre?.Trim().ToLowerInvariant();
        if (g is null || !Genres.Contains(g)) throw new RegleMetierException("Choisissez : problème, suggestion ou question.", "genre_retour");
        var e = string.IsNullOrWhiteSpace(ecran) ? "Général" : ecran.Trim()[..Math.Min(ecran.Trim().Length, 80)];
        await journal.EcrireAsync(utilisateurId, "retour_essai", null, null,
            new Dictionary<string, object?> { ["ecran"] = e, ["genre"] = g, ["texte"] = t }, null, ct);
    }

    public async Task<IReadOnlyList<RetourEssai>> ListerAsync(long utilisateurId, CancellationToken ct = default)
    {
        var tous = await droits.EstTitulaireAsync(utilisateurId, ct);
        return await retours.ListerAsync(tous ? null : utilisateurId, 500, ct);
    }
}
