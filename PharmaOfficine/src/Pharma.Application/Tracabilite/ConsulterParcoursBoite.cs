namespace Pharma.Application.Tracabilite;

public sealed record EtapeParcours(string Type, DateTime Date, string? Commentaire);

public sealed record ParcoursBoite(
    long BoiteId, string Identifiant, string Produit, string NumeroLot, DateOnly DatePeremption,
    string Fournisseur, string? NumeroBl, DateTime DateReception, string Statut, bool LotBloque,
    string? Ticket, DateTime? DateVente, string? NumFactureChifa, string? Patient,
    IReadOnlyList<EtapeParcours> Etapes, long ProduitId = 0);

public interface ITracabiliteLecture
{
    /// <summary>Boîte trouvée par son étiquette interne ou son numéro de série, avec son historique.</summary>
    Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default);
}

/// <summary>Parcours d'une boîte : grossiste, réception, vente, patient.</summary>
public sealed class ConsulterParcoursBoite(ITracabiliteLecture tracabilite)
{
    public Task<ParcoursBoite?> ExecuterAsync(string? code, CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(code)
            ? Task.FromResult<ParcoursBoite?>(null)
            : tracabilite.ParcoursAsync(code.Trim(), ct);
}
