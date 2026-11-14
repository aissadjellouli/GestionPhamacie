using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

public interface ICatalogueEcriture
{
    Task<bool> CodeBarreExisteAsync(string codeBarre, CancellationToken ct = default);
    Task<string?> DoublonAsync(string nom, string dosage, string forme, CancellationToken ct = default);
    /// <summary>
    /// Classe (psychotrope ou stupefiant) de la substance surveillée qui correspond à la DCI (table substance_surveillee, même test
    /// que la vue v_produit_a_verifier), sinon null.
    /// </summary>
    Task<string?> ClasseSurveilleeAsync(string dci, CancellationToken ct = default);
    /// <summary>
    /// Classe proposée par la nomenclature nationale (version la plus récente, vue v_nomenclature) pour ce n° d'enregistrement,
    /// seulement si elle est sous registre (psychotrope ou stupefiant, la plus stricte s'il y a plusieurs lignes) ; null sinon.
    /// </summary>
    Task<string?> ClasseNomenclatureAsync(string numEnregistrement, CancellationToken ct = default);
    /// <summary>
    /// Crée le produit ; la DCI est créée si elle n'existe pas encore. <paramref name="aVerifier"/> : produit invendable jusqu'à la
    /// confirmation de sa classe par le titulaire. <paramref name="classeConfirmeePar"/> : le titulaire a choisi une classe moins stricte
    /// que celle de la substance ou de la nomenclature ; sa confirmation est mémorisée (produit.classe_confirmee_par, _le).
    /// </summary>
    Task<long> CreerAsync(FicheProduit fiche, bool aVerifier, CancellationToken ct = default, long? classeConfirmeePar = null);
    Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default);
}

/// <summary>Produit créé ; Information : message au créateur (classe imposée, produit à vérifier), sinon null.</summary>
public sealed record ProduitCree(long Id, string? Information = null);

/// <summary>
/// Création d'un produit absent du catalogue (droit catalogue.creer), inscrite au journal. Créé par un autre que le titulaire avec
/// une DCI de substance surveillée, ou un n° d'enregistrement que la nomenclature nationale classe sous registre, et une classe moins
/// stricte (hors registre, ou psychotrope pour un stupéfiant) : classe imposée (psychotrope ou stupéfiant, la plus stricte des deux
/// sources), produit « à vérifier ». Créé ainsi par le titulaire : sa classe est gardée et sa confirmation mémorisée.
/// </summary>
public sealed class CreerProduit(ICatalogueEcriture catalogue, IPermissionService droits, IJournal journal)
{
    public async Task<ProduitCree> ExecuterAsync(FicheProduit saisie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "créer un produit", ct);
        var f = RegleProduit.Normaliser(saisie);

        if (f.CodeBarre is { } code && await catalogue.CodeBarreExisteAsync(code, ct))
            throw new RegleMetierException($"Le code-barres {code} est déjà utilisé par un produit du catalogue.");
        if (await catalogue.DoublonAsync(f.NomCommercial, f.Dosage, f.Forme, ct) is { } existant)
            throw new RegleMetierException($"« {existant} » existe déjà avec ce dosage et cette forme.");

        // Psychotrope créé en classe libre (ou liste I, II), ou stupéfiant créé en psychotrope, par un employé : il serait vendable sans
        // le registre (ou sous un registre moins strict) jusqu'à la vérification. Par le titulaire : sa classe est une confirmation mémorisée.
        var choisie = f.Classe;
        string? information = null;
        string? confirmeeContre = null;   // classe sous registre que le titulaire écarte en connaissance de cause
        var aVerifier = false;
        if (f.Classe != ClasseReglementaire.Stupefiant)
        {
            // DCI saisie, et classe que propose la nomenclature nationale pour le n° d'enregistrement : la plus stricte
            var parDci = await catalogue.ClasseSurveilleeAsync(f.Dci, ct);
            var parNomenclature = f.NumEnregistrement is { } num ? await catalogue.ClasseNomenclatureAsync(num, ct) : null;
            var retenue = RegleProduit.ClasseSurveilleeRetenue(parDci, parNomenclature);
            if (RegleProduit.MoinsStricteQueSurveillee(f.Classe, retenue))
            {
                if (await droits.EstTitulaireAsync(utilisateurId, ct))
                    confirmeeContre = retenue;
                else
                {
                    var imposee = CodesMetier.ClasseDepuisCode(retenue!);
                    f = f with { Classe = imposee, ListePsychotrope = null };
                    information = RegleProduit.MessageClasseImposee(f.Dci, imposee, parDci == imposee.Code() ? null : f.NumEnregistrement);
                    aVerifier = true;
                }
            }
        }

        var id = await catalogue.CreerAsync(f, aVerifier, ct, confirmeeContre is null ? null : utilisateurId);
        var details = new Dictionary<string, object?>
        {
            ["nom"] = f.NomCommercial, ["dci"] = f.Dci, ["dosage"] = f.Dosage, ["forme"] = f.Forme,
            ["prix_public"] = f.PrixPublic, ["classe"] = f.Classe.Code(), ["code_barre"] = f.CodeBarre,
            ["categorie"] = f.Categorie, ["liste_psychotrope"] = f.ListePsychotrope
        };
        if (aVerifier) { details["classe_choisie"] = choisie.Code(); details["a_verifier"] = true; }
        if (confirmeeContre is not null) { details["classe_confirmee"] = true; details["classe_proposee"] = confirmeeContre; }
        await journal.EcrireAsync(utilisateurId, "produit_cree", "produit", id, details, Permissions.CatalogueCreer, ct);
        return new ProduitCree(id, information);
    }
}

public sealed class RechercherDci(ICatalogueEcriture catalogue)
{
    public Task<IReadOnlyList<string>> ExecuterAsync(string? texte, CancellationToken ct = default) =>
        (texte?.Trim() ?? "").Length < 2 ? Task.FromResult<IReadOnlyList<string>>([]) : catalogue.DciAsync(texte!.Trim(), 10, ct);
}
