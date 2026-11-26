using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Lot O6 (offre de base et options) : packs de l'offre en vigueur vus du poste, relus toutes les 2 minutes au plus. Le serveur refuse
/// lui-même les actions d'un pack non inclus ; le poste s'en sert pour le bandeau « Option non incluse » et pour l'afficheur client (H).
/// Tant que l'offre n'a pas été lue, aucun pack n'est considéré comme inclus.
/// </summary>
public sealed class OffrePoste(ApiClient api)
{
    private static readonly TimeSpan Duree = TimeSpan.FromMinutes(2);
    private PacksOffreDto? etat;
    private DateTime luLeUtc;

    public PacksOffreDto? Etat => etat;

    public bool Inclus(string pack) => etat?.Packs.Contains(pack) == true;

    public string Libelle(string pack) => etat?.Tous?.FirstOrDefault(p => p.Code == pack)?.Libelle ?? pack;

    public async Task<PacksOffreDto?> ChargerAsync(bool forcer = false)
    {
        if (!forcer && etat is not null && DateTime.UtcNow - luLeUtc < Duree) return etat;
        var (d, _) = await api.PacksOffreAsync();
        if (d is not null) (etat, luLeUtc) = (d, DateTime.UtcNow);
        return etat;
    }

    /// <summary>Offre relue (après la saisie d'un code dans Mon offre).</summary>
    public void Oublier() => etat = null;
}
