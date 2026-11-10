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
    /// <summary>Crée le produit ; la DCI est créée si elle n'existe pas encore.</summary>
    Task<long> CreerAsync(FicheProduit fiche, CancellationToken ct = default);
    Task<IReadOnlyList<string>> DciAsync(string texte, int limite, CancellationToken ct = default);
}

/// <summary>Création d'un produit absent du catalogue (droit catalogue.creer), inscrite au journal.</summary>
public sealed class CreerProduit(ICatalogueEcriture catalogue, IPermissionService droits, IJournal journal)
{
    public async Task<long> ExecuterAsync(FicheProduit saisie, long utilisateurId, CancellationToken ct = default)
    {
        await droits.ExigerAsync(utilisateurId, Permissions.CatalogueCreer, "créer un produit", ct);
        var f = RegleProduit.Normaliser(saisie);

        if (f.CodeBarre is { } code && await catalogue.CodeBarreExisteAsync(code, ct))
            throw new RegleMetierException($"Le code-barres {code} est déjà utilisé par un produit du catalogue.");
        if (await catalogue.DoublonAsync(f.NomCommercial, f.Dosage, f.Forme, ct) is { } existant)
            throw new RegleMetierException($"« {existant} » existe déjà avec ce dosage et cette forme.");

        var id = await catalogue.CreerAsync(f, ct);
        await journal.EcrireAsync(utilisateurId, "produit_cree", "produit", id,
            new Dictionary<string, object?>
            {
                ["nom"] = f.NomCommercial, ["dci"] = f.Dci, ["dosage"] = f.Dosage, ["forme"] = f.Forme,
                ["prix_public"] = f.PrixPublic, ["classe"] = f.Classe.Code(), ["code_barre"] = f.CodeBarre
            }, Permissions.CatalogueCreer, ct);
        return id;
    }
}

public sealed class RechercherDci(ICatalogueEcriture catalogue)
{
    public Task<IReadOnlyList<string>> ExecuterAsync(string? texte, CancellationToken ct = default) =>
        (texte?.Trim() ?? "").Length < 2 ? Task.FromResult<IReadOnlyList<string>>([]) : catalogue.DciAsync(texte!.Trim(), 10, ct);
}
