namespace Pharma.Application.Tracabilite;

/// <summary>Avancement de l'étiquetage : part des boîtes en stock qui ont une identité.</summary>
public sealed record EtatEtiquetage(int BoitesIdentifiees, int BoitesEnStock, bool ModeStrict)
{
    public int Pourcentage => BoitesEnStock <= 0
        ? 0
        : (int)Math.Min(100, Math.Floor(100m * BoitesIdentifiees / BoitesEnStock));
}

public interface IEtiquetageLecture
{
    Task<EtatEtiquetage> EtatAsync(CancellationToken ct = default);
}

public sealed class ObtenirEtatEtiquetage(IEtiquetageLecture etiquetage)
{
    public Task<EtatEtiquetage> ExecuterAsync(CancellationToken ct = default) => etiquetage.EtatAsync(ct);
}
