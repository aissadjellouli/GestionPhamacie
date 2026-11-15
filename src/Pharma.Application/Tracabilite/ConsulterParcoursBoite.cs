using Pharma.Application.Securite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tracabilite;

public sealed record EtapeParcours(string Type, DateTime Date, string? Commentaire);

/// <param name="SousRegistre">Produit psychotrope ou stupéfiant (inscrit au registre).</param>
public sealed record ParcoursBoite(
    long BoiteId, string Identifiant, string Produit, string NumeroLot, DateOnly DatePeremption,
    string Fournisseur, string? NumeroBl, DateTime DateReception, string Statut, bool LotBloque,
    string? Ticket, DateTime? DateVente, string? NumFactureChifa, string? Patient,
    IReadOnlyList<EtapeParcours> Etapes, long ProduitId = 0, bool SousRegistre = false);

public interface ITracabiliteLecture
{
    /// <summary>Boîte trouvée par son étiquette interne ou son numéro de série, avec son historique.</summary>
    Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default);
}

/// <summary>
/// Parcours d'une boîte : grossiste, réception, vente, patient. Nom du patient (décision du titulaire, 2026-11-15) : psychotrope ou
/// stupéfiant, droit « psychotropes.registre » ; autre produit, droit « stock.declarer_perime » (celui du rappel de lot) ; le
/// titulaire le voit toujours. Sans le droit, le parcours reste visible mais le patient n'est pas envoyé (null).
/// </summary>
public sealed class ConsulterParcoursBoite(ITracabiliteLecture tracabilite, IPermissionService droits)
{
    public async Task<ParcoursBoite?> ExecuterAsync(string? code, long utilisateurId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var p = await tracabilite.ParcoursAsync(code.Trim(), ct);
        if (p?.Patient is null) return p;
        if (await droits.EstTitulaireAsync(utilisateurId, ct)) return p;
        var droit = p.SousRegistre ? Permissions.PsychotropesRegistre : Permissions.StockDeclarerPerime;
        return await droits.APermissionAsync(utilisateurId, droit, ct) ? p : p with { Patient = null };
    }
}
