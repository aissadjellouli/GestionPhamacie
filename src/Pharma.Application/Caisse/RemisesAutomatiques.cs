using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;
using Pharma.Domain.Reglages;
using Pharma.Domain.Securite;

namespace Pharma.Application.Caisse;

/// <summary>Remise automatique telle qu'on la liste : cible (produit ou catégorie), pourcentage, période, auteur, arrêt éventuel.</summary>
public sealed record RemiseAutomatique(long Id, long? ProduitId, string? Produit, string? Categorie, string? CategorieLibelle, decimal Pourcentage,
    DateOnly Debut, DateOnly Fin, string? Motif, string CreePar, DateTime CreeLe, DateTime? ArreteeLe, string? ArreteePar, string? ModeleCle = null);

/// <summary>Saisie d'une remise automatique : un produit OU une catégorie, pourcentage entier, du Debut au Fin inclus.</summary>
public sealed record RemiseAutomatiqueSaisie(long? ProduitId, string? Categorie, decimal Pourcentage, DateOnly Debut, DateOnly Fin, string? Motif);

/// <summary>Produit visé : nom et remboursable (jamais de remise automatique sur un produit remboursable).</summary>
public sealed record ProduitRemise(string Nom, bool Remboursable);

public interface IRemisesAutomatiquesAdmin
{
    Task<IReadOnlyList<RemiseAutomatique>> ListerAsync(CancellationToken ct = default);
    Task<RemiseAutomatique?> LireAsync(long id, CancellationToken ct = default);
    Task<ProduitRemise?> ProduitAsync(long produitId, CancellationToken ct = default);
    Task<string?> CategorieAsync(string code, CancellationToken ct = default);
    /// <summary>Autre remise en cours ou à venir (non arrêtée) sur la même cible, dont la période chevauche celle-ci.</summary>
    Task<bool> ChevaucheAsync(RemiseAutomatiqueSaisie s, long? saufId, CancellationToken ct = default);
    Task<long> CreerAsync(RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default);
    Task ModifierAsync(long id, RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default);
    Task ArreterAsync(long id, long auteurId, CancellationToken ct = default);
}

public sealed class ListerRemisesAutomatiques(IRemisesAutomatiquesAdmin admin)
{
    public Task<IReadOnlyList<RemiseAutomatique>> ExecuterAsync(CancellationToken ct = default) => admin.ListerAsync(ct);
}

/// <summary>
/// Remises automatiques : décidées par le titulaire ou un employé à qui il en a donné le droit (caisse.remises_auto, réglé sur l'employé).
/// Pourcentage entier de 1 à 100, produit OU catégorie, période du début à la fin incluse, jamais sur un produit remboursable, jamais deux
/// remises qui se chevauchent sur la même cible. Chaque création, modification ou arrêt est inscrit au journal.
/// </summary>
public sealed class GererRemisesAutomatiques(IRemisesAutomatiquesAdmin admin, IPermissionService droits, IJournal journal, IHorloge horloge,
    Reglages.IGardeReglagesCentraux? garde = null)
{
    private async Task<(string Cible, RemiseAutomatiqueSaisie Saisie)> VerifierAsync(RemiseAutomatiqueSaisie s, long? saufId, CancellationToken ct)
    {
        RegleRemisesAutomatiques.VerifierCibleExclusive(s.ProduitId is not null, s.Categorie);
        RegleRemisesAutomatiques.VerifierPourcentage(s.Pourcentage);
        RegleRemisesAutomatiques.VerifierPeriode(s.Debut, s.Fin, horloge.Aujourdhui);
        var motif = RegleRemisesAutomatiques.NettoyerMotif(s.Motif);

        string cible;
        if (s.ProduitId is { } pid)
        {
            var p = await admin.ProduitAsync(pid, ct) ?? throw new RegleMetierException("Produit introuvable.", "cible_remise");
            RegleRemisesAutomatiques.VerifierProduit(p.Nom, p.Remboursable);
            cible = p.Nom;
        }
        else
            cible = await admin.CategorieAsync(s.Categorie!.Trim(), ct) ?? throw new RegleMetierException("Catégorie inconnue.", "cible_remise");

        var saisie = s with { Categorie = s.Categorie?.Trim(), Motif = motif };
        RegleRemisesAutomatiques.ExigerSansChevauchement(cible, await admin.ChevaucheAsync(saisie, saufId, ct));
        return (cible, saisie);
    }

    private static Dictionary<string, object?> Details(string cible, RemiseAutomatiqueSaisie s) => new()
    {
        ["cible"] = cible, ["produit_id"] = s.ProduitId, ["categorie"] = s.Categorie, ["pourcentage"] = s.Pourcentage,
        ["debut"] = s.Debut.ToString("yyyy-MM-dd"), ["fin"] = s.Fin.ToString("yyyy-MM-dd"), ["motif"] = s.Motif
    };

    public async Task<long> CreerAsync(RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(auteurId, Permissions.CaisseRemisesAuto, "créer une remise automatique", ct);
        if (garde is not null) await garde.ExigerAjoutLocalAsync(Pharma.Domain.Reglages.TablesMutualisables.Remises, ct);
        var (cible, saisie) = await VerifierAsync(s, null, ct);
        var id = await admin.CreerAsync(saisie, auteurId, ct);
        var creation = Details(cible, saisie);
        creation["avant"] = null;
        creation["apres"] = new { pourcentage = saisie.Pourcentage, debut = saisie.Debut.ToString("yyyy-MM-dd"), fin = saisie.Fin.ToString("yyyy-MM-dd"), cible };
        await journal.EcrireAsync(auteurId, "remise_automatique_creee", "remise_automatique", id, creation, Permissions.CaisseRemisesAuto, ct);
        return id;
    }

    public async Task ModifierAsync(long id, RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(auteurId, Permissions.CaisseRemisesAuto, "modifier une remise automatique", ct);
        var avant = await admin.LireAsync(id, ct) ?? throw new RegleMetierException("Remise automatique introuvable.");
        if (avant.ArreteeLe is not null) throw new RegleMetierException("Cette remise est arrêtée : créez-en une nouvelle.");
        if (garde is not null) await garde.ExigerElementLocalAsync(Pharma.Domain.Reglages.TablesMutualisables.Remises, avant.ModeleCle is not null, ct);
        var (cible, saisie) = await VerifierAsync(s, id, ct);
        await admin.ModifierAsync(id, saisie, auteurId, ct);
        var details = Details(cible, saisie);
        details["apres"] = new { pourcentage = saisie.Pourcentage, debut = saisie.Debut.ToString("yyyy-MM-dd"), fin = saisie.Fin.ToString("yyyy-MM-dd"), cible };
        details["avant"] = new { pourcentage = avant.Pourcentage, debut = avant.Debut.ToString("yyyy-MM-dd"), fin = avant.Fin.ToString("yyyy-MM-dd"), cible = avant.Produit ?? avant.CategorieLibelle };
        await journal.EcrireAsync(auteurId, "remise_automatique_modifiee", "remise_automatique", id, details, Permissions.CaisseRemisesAuto, ct);
    }

    /// <summary>Arrêt immédiat : la remise ne s'applique plus aux ventes suivantes (elle reste dans la liste, « arrêtée »).</summary>
    public async Task ArreterAsync(long id, long auteurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(auteurId, Permissions.CaisseRemisesAuto, "arrêter une remise automatique", ct);
        var r = await admin.LireAsync(id, ct) ?? throw new RegleMetierException("Remise automatique introuvable.");
        if (r.ArreteeLe is not null) return;
        if (garde is not null) await garde.ExigerElementLocalAsync(Pharma.Domain.Reglages.TablesMutualisables.Remises, r.ModeleCle is not null, ct);
        await admin.ArreterAsync(id, auteurId, ct);
        await journal.EcrireAsync(auteurId, "remise_automatique_arretee", "remise_automatique", id,
            new Dictionary<string, object?>
            {
                ["cible"] = r.Produit ?? r.CategorieLibelle, ["pourcentage"] = r.Pourcentage,
                ["avant"] = new { arretee = false }, ["apres"] = new { arretee = true }
            }, Permissions.CaisseRemisesAuto, ct);
    }
}
