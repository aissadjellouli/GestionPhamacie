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
    /// <summary>Nouvelle classe d'un produit ; renvoie l'ancienne (null si produit introuvable).</summary>
    Task<string?> ChangerClasseAsync(long produitId, string classe, CancellationToken ct = default);
}

/// <summary>Import du fichier Excel de la nomenclature (titulaire), inscrit au journal.</summary>
public sealed class ImporterNomenclature(ILecteurNomenclature lecteur, INomenclature nomenclature, IPermissionService droits, IJournal journal)
{
    public const long TailleMax = 40 * 1024 * 1024;

    public async Task<VersionNomenclature> ExecuterAsync(Stream contenu, string? fichier, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "importer la nomenclature nationale", ct);
        NomenclatureLue lue;
        try { lue = lecteur.Lire(contenu); }
        catch (FormatException e) { throw new RegleMetierException(e.Message); }
        Nomenclature.Verifier(lue.Lignes);

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
public sealed class RechercherNomenclature(INomenclature nomenclature)
{
    public Task<IReadOnlyList<MedicamentNomenclature>> ExecuterAsync(string? texte, CancellationToken ct = default) =>
        (texte?.Trim() ?? "").Length < 2
            ? Task.FromResult<IReadOnlyList<MedicamentNomenclature>>([])
            : nomenclature.RechercherAsync(texte!.Trim(), 15, ct);
}

/// <summary>Le titulaire applique la classe proposée (ex. psychotrope) à un produit du catalogue ; inscrit au journal.</summary>
public sealed class AppliquerClasseNomenclature(INomenclature nomenclature, IPermissionService droits, IJournal journal)
{
    public async Task ExecuterAsync(long produitId, string? classe, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerTitulaireAsync(utilisateurId, "changer la classe d'un produit", ct);
        ClasseReglementaire c;
        try { c = CodesMetier.ClasseDepuisCode(classe ?? ""); }
        catch (ArgumentException) { throw new RegleMetierException($"Classe inconnue : {classe}."); }
        var ancienne = await nomenclature.ChangerClasseAsync(produitId, c.Code(), ct)
                       ?? throw new RegleMetierException("Produit introuvable.");
        await journal.EcrireAsync(utilisateurId, "classe_produit", "produit", produitId,
            new Dictionary<string, object?> { ["ancienne"] = ancienne, ["nouvelle"] = c.Code(), ["source"] = "nomenclature" }, null, ct);
    }
}
