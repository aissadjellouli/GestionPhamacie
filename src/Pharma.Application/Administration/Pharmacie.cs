using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Administration;

/// <summary>Identité de la pharmacie (table pharmacie). Langue : ar, fr ou en.</summary>
public sealed record IdentitePharmacie(
    string? Nom, string? Titulaire, string? Adresse, string? Telephone, string? Nif, string? ChifaOfficine, string LangueDefaut,
    string? Rc = null, string? Nis = null, string? ArticleImposition = null);

public interface IPharmacie
{
    Task<IdentitePharmacie> LireAsync(CancellationToken ct = default);
    Task EnregistrerAsync(IdentitePharmacie identite, CancellationToken ct = default);
}

public sealed class ObtenirPharmacie(IPharmacie pharmacie)
{
    public Task<IdentitePharmacie> ExecuterAsync(CancellationToken ct = default) => pharmacie.LireAsync(ct);
}

public sealed class ModifierPharmacie(IPharmacie pharmacie, IJournal journal)
{
    /// <summary>Langues dont l'interface est traduite. L'arabe et l'anglais viendront avec les traductions.</summary>
    public static readonly IReadOnlySet<string> LanguesDisponibles = new HashSet<string> { "fr" };

    public async Task ExecuterAsync(IdentitePharmacie i, long auteurId, CancellationToken ct = default)
    {
        if (!LanguesDisponibles.Contains(i.LangueDefaut))
            throw new RegleMetierException("Seul le français est disponible pour l’instant : les traductions arabe et anglaise sont à venir.");
        static string? Net(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var propre = new IdentitePharmacie(Net(i.Nom), Net(i.Titulaire), Net(i.Adresse), Net(i.Telephone), Net(i.Nif),
            Net(i.ChifaOfficine), i.LangueDefaut, Net(i.Rc), Net(i.Nis), Net(i.ArticleImposition));

        var avant = await pharmacie.LireAsync(ct);
        await pharmacie.EnregistrerAsync(propre, ct);
        await journal.EcrireAsync(auteurId, "pharmacie_modifiee", "pharmacie", 1,
            new Dictionary<string, object?> { ["avant"] = avant, ["apres"] = propre }, Permissions.AdminParametres, ct);
    }
}
