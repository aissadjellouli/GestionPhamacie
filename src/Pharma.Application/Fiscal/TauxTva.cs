using System.Globalization;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Fiscal;

/// <summary>Taux de la liste ; Produits : nombre de produits qui l'utilisent.</summary>
public sealed record TauxTvaLu(long Id, decimal Taux, string Libelle, bool Actif, int Produits);

/// <summary>Taux proposé à la création d'un produit de la catégorie ; null : à choisir.</summary>
public sealed record TauxTvaCategorie(string Code, string Libelle, decimal? Taux);

/// <summary>Liste des taux (script 171), taux par défaut des catégories, et taux utilisés hors de la liste.</summary>
public sealed record ListeTauxTva(IReadOnlyList<TauxTvaLu> Taux, IReadOnlyList<TauxTvaCategorie> Categories, IReadOnlyList<(decimal Taux, int Produits)> HorsListe);

public interface ITauxTva
{
    Task<ListeTauxTva> ListerAsync(CancellationToken ct = default);
    /// <summary>Null si ce taux existe déjà.</summary>
    Task<long?> AjouterAsync(decimal taux, string libelle, CancellationToken ct = default);
    /// <summary>Faux si le taux n'existe pas.</summary>
    Task<bool> ModifierAsync(long id, string libelle, bool actif, CancellationToken ct = default);
    /// <summary>Taux par défaut d'une catégorie (null : retiré) ; faux si la catégorie n'existe pas.</summary>
    Task<bool> DefinirDefautAsync(string categorie, decimal? taux, CancellationToken ct = default);
}

/// <summary>
/// Taux de TVA réglables (décision du titulaire du 2026-11-26, « go TVA », script 171) : Administration › Réglages fiscaux. Titulaire seul
/// pour modifier ; la liste se lit par tout employé connecté (formulaires). Un taux n'est jamais supprimé : désactivé, il n'est plus
/// proposé ; il ne peut pas l'être tant qu'il est le taux par défaut d'une catégorie. Chaque changement est inscrit au journal.
/// </summary>
public sealed class GererTauxTva(ITauxTva depot, IPermissionService droits, IJournal journal)
{
    public const int LibelleMax = 60;

    public Task<ListeTauxTva> ListerAsync(CancellationToken ct = default) => depot.ListerAsync(ct);

    public async Task<long> AjouterAsync(long utilisateurId, decimal taux, string? libelle, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les taux de TVA", ct);
        Verifier(taux);
        var l = Libelle(libelle);
        var id = await depot.AjouterAsync(taux, l, ct) ?? throw new RegleMetierException($"Le taux {Texte(taux)} % est déjà dans la liste.", "taux");
        await journal.EcrireAsync(utilisateurId, "taux_tva_ajoute", "taux_tva", id, new Dictionary<string, object?> { ["taux"] = taux, ["libelle"] = l }, null, ct);
        return id;
    }

    public async Task ModifierAsync(long utilisateurId, long id, string? libelle, bool actif, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les taux de TVA", ct);
        var l = Libelle(libelle);
        var liste = await depot.ListerAsync(ct);
        var avant = liste.Taux.FirstOrDefault(t => t.Id == id) ?? throw new RegleMetierException("Taux introuvable : rechargez la liste.");
        if (!actif && liste.Categories.FirstOrDefault(c => c.Taux == avant.Taux) is { } cat)
            throw new RegleMetierException($"Ce taux est le taux par défaut de « {cat.Libelle} » : choisissez-en un autre pour cette catégorie avant de le désactiver.", "actif");
        if (!actif && liste.Taux.Count(t => t.Actif) == 1 && avant.Actif)
            throw new RegleMetierException("Il faut au moins un taux actif.", "actif");
        if (avant.Libelle == l && avant.Actif == actif) return;
        if (!await depot.ModifierAsync(id, l, actif, ct)) throw new RegleMetierException("Taux introuvable : rechargez la liste.");
        await journal.EcrireAsync(utilisateurId, "taux_tva_modifie", "taux_tva", id, new Dictionary<string, object?>
        {
            ["taux"] = avant.Taux, ["libelle_avant"] = avant.Libelle, ["libelle_apres"] = l, ["actif_avant"] = avant.Actif, ["actif_apres"] = actif
        }, null, ct);
    }

    public async Task DefinirDefautAsync(long utilisateurId, string? categorie, decimal? taux, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "régler les taux de TVA", ct);
        var liste = await depot.ListerAsync(ct);
        var cat = liste.Categories.FirstOrDefault(c => c.Code == categorie) ?? throw new RegleMetierException("Catégorie inconnue.", "categorie");
        if (taux is { } t && !liste.Taux.Any(x => x.Taux == t && x.Actif))
            throw new RegleMetierException($"Le taux {Texte(t)} % n’est pas un taux actif de la liste.", "taux");
        if (cat.Taux == taux) return;
        if (!await depot.DefinirDefautAsync(cat.Code, taux, ct)) throw new RegleMetierException("Catégorie inconnue.", "categorie");
        await journal.EcrireAsync(utilisateurId, "taux_tva_categorie", "taux_tva_categorie", 0, new Dictionary<string, object?>
        {
            ["categorie"] = cat.Code, ["avant"] = cat.Taux, ["apres"] = taux
        }, null, ct);
    }

    public static void Verifier(decimal taux)
    {
        if (taux is < 0 or > 100) throw new RegleMetierException("Taux de TVA entre 0 et 100 %.", "taux");
        if (decimal.Round(taux, 2) != taux) throw new RegleMetierException("Taux de TVA : 2 décimales au plus.", "taux");
    }

    private static string Libelle(string? libelle)
    {
        var l = libelle?.Trim() ?? "";
        if (l.Length == 0) throw new RegleMetierException("Donnez un libellé au taux (ex. « Taux normal »).", "libelle");
        if (l.Length > LibelleMax) throw new RegleMetierException($"Libellé : {LibelleMax} caractères au plus.", "libelle");
        return l;
    }

    public static string Texte(decimal taux) => taux.ToString("0.##", CultureInfo.GetCultureInfo("fr-FR"));
}

/// <summary>
/// Contrôle du taux saisi (fiche produit, nouveau produit, prestation, ligne libre de facture) : un taux actif de la liste ; un taux déjà
/// enregistré hors de la liste est gardé s'il ne change pas (rien n'est modifié dans l'existant, script 171).
/// </summary>
public sealed class TauxTvaAutorises(ITauxTva depot)
{
    public async Task ExigerAsync(decimal taux, decimal? actuel, string champ, CancellationToken ct = default)
    {
        if (actuel is { } a && a == taux) return;
        var actifs = (await depot.ListerAsync(ct)).Taux.Where(t => t.Actif).Select(t => t.Taux).ToList();
        if (actifs.Contains(taux)) return;
        throw new RegleMetierException($"Taux de TVA {GererTauxTva.Texte(taux)} % absent de la liste (Administration › Réglages fiscaux) : choisissez "
            + string.Join(" ou ", actifs.Select(t => GererTauxTva.Texte(t) + " %")) + ".", champ);
    }
}
