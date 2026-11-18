using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Catalogue;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;

namespace Pharma.Application.Catalogue;

/// <summary>Fichier lu : nom de la feuille principale (ex. « Nomenclature Juin 2026 ») et lignes des trois feuilles.</summary>
public sealed record NomenclatureLue(string Libelle, IReadOnlyList<LigneNomenclature> Lignes);

public sealed record VersionNomenclature(string Libelle, string Fichier, int EnVigueur, int NonRenouveles, int Retraits,
    DateTime ImporteeLe, string ImporteePar);

/// <summary>Médicament de la nomenclature proposé à la création d'un produit.</summary>
public sealed record MedicamentNomenclature(
    string? NumEnregistrement, string? Code, string? Dci, string? Marque, string? Forme, string? Dosage, string? Conditionnement,
    string? Liste, string? Laboratoire, string? Pays, string Situation, string ClasseProposee, bool DejaAuCatalogue);

/// <summary>Produit du catalogue à revoir : substance surveillée mal classée, ou retiré / non renouvelé.</summary>
public sealed record ProduitAVerifier(long ProduitId, string Nom, string? Dosage, string? Forme, string? Dci,
    string? NumEnregistrement, string Classe, string? ClasseProposee, string? Situation, string Motif);

public interface ILecteurNomenclature
{
    /// <summary>Lit le classeur Excel officiel ; FormatException si ce n'est pas un .xlsx lisible.</summary>
    NomenclatureLue Lire(Stream contenu);
}

public interface INomenclature
{
    /// <summary>Remplace la nomenclature par la nouvelle version, en une transaction ; renvoie l'id de version.</summary>
    Task<long> RemplacerAsync(NomenclatureLue n, string fichier, long utilisateurId, CancellationToken ct = default);
    Task<VersionNomenclature?> VersionAsync(CancellationToken ct = default);
    Task<IReadOnlyList<MedicamentNomenclature>> RechercherAsync(string texte, int limite, CancellationToken ct = default);
    Task<IReadOnlyList<ProduitAVerifier>> AVerifierAsync(CancellationToken ct = default);
    /// <summary>
    /// Nouvelle classe d'un produit ; renvoie l'ancienne (null si produit introuvable). <paramref name="confirmation"/> : classe moins
    /// stricte que celle de la substance ou de la nomenclature, choisie par le titulaire : mémorisée (produit.classe_confirmee_par, _le) ;
    /// sinon la confirmation précédente éventuelle est effacée.
    /// </summary>
    Task<string?> ChangerClasseAsync(long produitId, string classe, long utilisateurId, CancellationToken ct = default, bool confirmation = false);
    /// <summary>
    /// Classe sous registre (psychotrope ou stupefiant) que donnent au produit sa DCI (substance surveillée) et la nomenclature nationale
    /// (n° d'enregistrement), la plus stricte des deux ; null si aucune, ou produit introuvable.
    /// </summary>
    Task<string?> ClasseSurveilleeProduitAsync(long produitId, CancellationToken ct = default);
}

/// <summary>Import du fichier Excel de la nomenclature (titulaire), inscrit au journal ; aussi par un ordre du serveur central (lot S5.3).</summary>
public sealed class ImporterNomenclature(ILecteurNomenclature lecteur, INomenclature nomenclature, IPermissionService droits, IJournal journal)
{
    public const long TailleMax = 40 * 1024 * 1024;

    public async Task<VersionNomenclature> ExecuterAsync(Stream contenu, string? fichier, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer la nomenclature nationale", ct);
        return await RemplacerAsync(Lire(contenu), fichier, utilisateurId, ct);
    }

    /// <summary>
    /// Lot S5.3 (ordre du serveur central) : même import, sauf si la version chargée est déjà celle-ci (même libellé et même nombre
    /// de lignes) : rien ne change, « déjà à jour » est inscrit au journal et null est renvoyé (décision S5.3-D5).
    /// </summary>
    public async Task<VersionNomenclature?> ImporterSiNouvelleAsync(Stream contenu, string? fichier, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer la nomenclature nationale", ct);
        var lue = Lire(contenu);
        if (await nomenclature.VersionAsync(ct) is { } v && v.Libelle == lue.Libelle && v.EnVigueur + v.NonRenouveles + v.Retraits == lue.Lignes.Count)
        {
            await journal.EcrireAsync(utilisateurId, "nomenclature_deja_a_jour", "nomenclature_version", null,
                new Dictionary<string, object?> { ["version"] = v.Libelle, ["source"] = "ordre du serveur central" }, null, ct);
            return null;
        }
        return await RemplacerAsync(lue, fichier, utilisateurId, ct);
    }

    private NomenclatureLue Lire(Stream contenu)
    {
        NomenclatureLue lue;
        try { lue = lecteur.Lire(contenu); }
        catch (FormatException e) { throw new RegleMetierException(e.Message); }
        Nomenclature.Verifier(lue.Lignes);
        return lue;
    }

    private async Task<VersionNomenclature> RemplacerAsync(NomenclatureLue lue, string? fichier, long utilisateurId, CancellationToken ct)
    {
        var nom = string.IsNullOrWhiteSpace(fichier) ? "nomenclature.xlsx" : Path.GetFileName(fichier.Trim());
        var id = await nomenclature.RemplacerAsync(lue, nom, utilisateurId, ct);
        var version = await nomenclature.VersionAsync(ct) ?? throw new InvalidOperationException("Version introuvable après import.");
        await journal.EcrireAsync(utilisateurId, "nomenclature_importee", "nomenclature_version", id,
            new Dictionary<string, object?>
            {
                ["version"] = version.Libelle, ["fichier"] = nom, ["en_vigueur"] = version.EnVigueur,
                ["non_renouveles"] = version.NonRenouveles, ["retraits"] = version.Retraits
            }, null, ct);
        return version;
    }
}

public sealed record EtatNomenclature(VersionNomenclature? Version, IReadOnlyList<ProduitAVerifier> AVerifier);

public sealed class ObtenirEtatNomenclature(INomenclature nomenclature, IPermissionService droits)
{
    public async Task<EtatNomenclature> ExecuterAsync(long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "consulter la nomenclature nationale", ct);
        return new EtatNomenclature(await nomenclature.VersionAsync(ct), await nomenclature.AVerifierAsync(ct));
    }
}

/// <summary>Recherche par nom de marque, DCI ou n° d'enregistrement, pour créer un produit (droit catalogue.creer).</summary>
/// <summary>Une version de la nomenclature est-elle importée ? (message « pas encore importée » de la nouvelle fiche produit)</summary>
public sealed class NomenclatureImportee(INomenclature nomenclature)
{
    public async Task<bool> ExecuterAsync(CancellationToken ct = default) => await nomenclature.VersionAsync(ct) is not null;
}

public sealed class RechercherNomenclature(INomenclature nomenclature)
{
    public Task<IReadOnlyList<MedicamentNomenclature>> ExecuterAsync(string? texte, CancellationToken ct = default) =>
        (texte?.Trim() ?? "").Length < 2
            ? Task.FromResult<IReadOnlyList<MedicamentNomenclature>>([])
            : nomenclature.RechercherAsync(texte!.Trim(), 15, ct);
}

/// <summary>
/// Le titulaire applique la classe proposée (ex. psychotrope) à un produit du catalogue, ou confirme sa classe ; inscrit au journal.
/// Classe moins stricte que celle de la substance surveillée (DCI) ou de la nomenclature (n° d'enregistrement) : confirmation
/// mémorisée, le produit n'est plus « à vérifier » et une modification par un employé ne le reclasse plus (tant que sa DCI ne change pas).
/// </summary>
public sealed class AppliquerClasseNomenclature(INomenclature nomenclature, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long produitId, string? classe, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "changer la classe d'un produit", ct);
        ClasseReglementaire c;
        try { c = CodesMetier.ClasseDepuisCode(classe ?? ""); }
        catch (ArgumentException) { throw new RegleMetierException($"Classe inconnue : {classe}."); }
        var proposee = await nomenclature.ClasseSurveilleeProduitAsync(produitId, ct);
        var confirmation = RegleProduit.MoinsStricteQueSurveillee(c, proposee);
        var ancienne = await nomenclature.ChangerClasseAsync(produitId, c.Code(), utilisateurId, ct, confirmation)
                       ?? throw new RegleMetierException("Produit introuvable.");
        var details = new Dictionary<string, object?> { ["ancienne"] = ancienne, ["nouvelle"] = c.Code(), ["source"] = "nomenclature" };
        if (confirmation) { details["classe_confirmee"] = true; details["classe_proposee"] = proposee; }
        await journal.EcrireAsync(utilisateurId, "classe_produit", "produit", produitId, details, null, ct);
    }
}
