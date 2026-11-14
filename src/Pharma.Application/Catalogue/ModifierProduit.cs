using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>
/// Fiche modifiable d'un produit du catalogue (la classe réglementaire se confirme à part, par le titulaire). NumEnregistrement : lu
/// en base, jamais modifié ici.
/// </summary>
public sealed record FicheProduitModifiable(long Id, string NomCommercial, string? Dci, string? Dosage, string? Forme, string? CodeBarre,
    decimal PrixPublic, decimal? TarifReference, bool Remboursable, decimal TauxTva, bool Actif, string Classe = "libre", string? NumEnregistrement = null);

public interface IProduitsModification
{
    Task<FicheProduitModifiable?> LireAsync(long id, CancellationToken ct = default);
    Task<bool> CodeBarreAutreProduitAsync(string codeBarre, long id, CancellationToken ct = default);
    /// <summary>Classe (psychotrope ou stupefiant) de la substance surveillée qui correspond à la DCI (table substance_surveillee), sinon null.</summary>
    Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default);
    /// <summary>Classe sous registre proposée par la nomenclature nationale pour ce n° d'enregistrement (voir ICatalogueEcriture), sinon null.</summary>
    Task<string?> ClasseNomenclatureAsync(string numEnregistrement, CancellationToken ct = default);
    /// <summary>
    /// Enregistre la fiche ; la DCI est créée si elle n'existe pas encore. La classe n'est écrite que si elle est imposée
    /// (<paramref name="classeImposee"/>) : le produit devient alors « à vérifier » et, s'il a du stock, reçoit son solde
    /// d'ouverture au registre (une fois par produit), inscrit au nom de <paramref name="utilisateurId"/>.
    /// </summary>
    Task ModifierAsync(FicheProduitModifiable f, bool classeImposee, long utilisateurId, CancellationToken ct = default);
}

public sealed class LireFicheProduit(IProduitsModification produits)
{
    public async Task<FicheProduitModifiable> ExecuterAsync(long id, CancellationToken ct = default) =>
        await produits.LireAsync(id, ct) ?? throw new RegleMetierException("Produit introuvable.");
}

/// <summary>
/// Modification d'un produit (droit de créer un produit) ; changer le prix public demande en plus le droit de modifier un prix de
/// vente. Un produit désactivé n'est plus proposé à la vente ni aux commandes. Chaque champ changé est inscrit au journal.
/// Un autre que le titulaire qui modifie un produit dont la classe est hors registre (que la DCI change ou non) : si sa DCI est celle
/// d'une substance surveillée, ou si la nomenclature nationale classe sous registre le n° d'enregistrement du produit, classe imposée
/// (psychotrope ou stupéfiant, la plus stricte) et produit « à vérifier » ; renvoie alors le message à lui afficher (sinon null).
/// </summary>
public sealed class ModifierProduit(IProduitsModification produits, IPermissionService droits, IJournal journal, Surveillance.Surveiller? surveillance = null)
{
    public async Task<string?> ExecuterAsync(FicheProduitModifiable saisie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "modifier un produit", ct);
        var actuel = await produits.LireAsync(saisie.Id, ct) ?? throw new RegleMetierException("Produit introuvable.");
        static string? N(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        var f = saisie with { NomCommercial = saisie.NomCommercial?.Trim() ?? "", Dci = N(saisie.Dci), Dosage = N(saisie.Dosage), Forme = N(saisie.Forme),
            CodeBarre = N(saisie.CodeBarre), Classe = actuel.Classe, NumEnregistrement = actuel.NumEnregistrement };
        if (f.NomCommercial.Length == 0) throw new RegleMetierException("Nom commercial obligatoire.", "nom_commercial");
        if (f.NomCommercial.Length > 120) throw new RegleMetierException("Nom commercial : 120 caractères au plus.", "nom_commercial");
        if (f.PrixPublic <= 0) throw new RegleMetierException("Le prix public doit être supérieur à 0.", "prix_public");
        if (f.TarifReference is <= 0) throw new RegleMetierException("Le tarif de référence doit être supérieur à 0.", "tarif_reference");
        if (f.Remboursable && f.TarifReference is null) throw new RegleMetierException("Un produit remboursable a besoin d’un tarif de référence.", "tarif_reference");
        if (f.TauxTva is < 0 or > 100) throw new RegleMetierException("Taux de TVA entre 0 et 100 %.", "tva");
        if (f.CodeBarre is { } code && code != actuel.CodeBarre)
        {
            if (code.Length == 14 && code[0] == '0') f = f with { CodeBarre = code = code[1..] };
            if (!Ean13.EstValide(code)) throw new RegleMetierException("Code-barres invalide : 13 chiffres avec une clé de contrôle correcte.", "code_barre");
            if (Ean13.Parse(code).EstInterne) throw new RegleMetierException("Les codes commençant par 20 sont réservés aux étiquettes internes de l’officine.", "code_barre");
            if (await produits.CodeBarreAutreProduitAsync(code, f.Id, ct))
                throw new RegleMetierException($"Le code-barres {code} est déjà utilisé par un autre produit.", "code_barre");
        }
        if (f.PrixPublic != actuel.PrixPublic)
            await droits.ExigerAsync(utilisateurId, Permissions.PrixModifier, "modifier un prix de vente", ct);

        // Toute modification par un autre que le titulaire d'un produit hors registre (DCI changée ou non) : DCI de psychotrope ou de
        // stupéfiant, ou n° d'enregistrement classé sous registre par la nomenclature (une DCI anodine ne suffit pas à l'en sortir),
        // la plus stricte l'emportant : classe imposée, à vérifier, comme à la création
        string? information = null;
        if (actuel.Classe is not ("psychotrope" or "stupefiant") && !await droits.EstTitulaireAsync(utilisateurId, ct))
        {
            ClasseReglementaire choisie;
            try { choisie = CodesMetier.ClasseDepuisCode(actuel.Classe); }
            catch (ArgumentException) { choisie = ClasseReglementaire.Libre; }
            var parDci = f.Dci is { } dci ? await produits.ClasseSurveilleeAsync(dci, ct) : null;
            var parNomenclature = actuel.NumEnregistrement is { Length: > 0 } num ? await produits.ClasseNomenclatureAsync(num, ct) : null;
            if (RegleProduit.ClasseImposee(choisie, RegleProduit.ClasseSurveilleeRetenue(parDci, parNomenclature), false) is { } imposee)
            {
                f = f with { Classe = imposee.Code() };
                information = RegleProduit.MessageClasseImposee(f.Dci ?? "", imposee, parDci == imposee.Code() ? null : actuel.NumEnregistrement);
            }
        }

        await produits.ModifierAsync(f, information is not null, utilisateurId, ct);

        var changes = new Dictionary<string, object?> { ["produit"] = f.NomCommercial };
        void Comparer(string nom, object? avant, object? apres) { if (!Equals(avant, apres)) changes[nom] = new { avant, apres }; }
        Comparer("nom_commercial", actuel.NomCommercial, f.NomCommercial); Comparer("dci", actuel.Dci, f.Dci);
        Comparer("dosage", actuel.Dosage, f.Dosage); Comparer("forme", actuel.Forme, f.Forme); Comparer("code_barre", actuel.CodeBarre, f.CodeBarre);
        Comparer("tarif_reference", actuel.TarifReference, f.TarifReference); Comparer("remboursable", actuel.Remboursable, f.Remboursable);
        Comparer("taux_tva", actuel.TauxTva, f.TauxTva); Comparer("actif", actuel.Actif, f.Actif);
        Comparer("classe", actuel.Classe, f.Classe);
        if (information is not null) changes["a_verifier"] = true;
        if (changes.Count > 1)
            await journal.EcrireAsync(utilisateurId, "produit_modifie", "produit", f.Id, changes, Permissions.CatalogueCreer, ct);
        if (f.PrixPublic != actuel.PrixPublic)
            await journal.EcrireAsync(utilisateurId, "prix_modifie", "produit", f.Id,
                new Dictionary<string, object?> { ["produit"] = f.NomCommercial, ["avant"] = actuel.PrixPublic, ["apres"] = f.PrixPublic, ["source"] = "fiche produit" },
                Permissions.PrixModifier, ct);
        // Surveillance : baisse de prix par un autre que le titulaire
        if (f.PrixPublic < actuel.PrixPublic && surveillance is not null) await surveillance.ExecuterAsync(ct);
        return information;
    }
}
