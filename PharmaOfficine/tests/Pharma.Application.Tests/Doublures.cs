using Pharma.Application.Catalogue;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Securite;
using Pharma.Domain.Stock;

namespace Pharma.Application.Tests;

internal sealed class HorlogeFixe(DateOnly aujourdhui) : IHorloge
{
    public DateTime MaintenantUtc => aujourdhui.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);
    public DateOnly Aujourdhui => aujourdhui;
}

internal sealed class LotsEnMemoire(params Lot[] lots) : ILotsLecture
{
    public Task<IReadOnlyList<Lot>> LotsEnStockAsync(long produitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Lot>>(lots.Where(l => l.ProduitId == produitId).ToList());
}

internal sealed class CatalogueEspion : ICatalogueLecture
{
    public string? Texte { get; private set; }
    public int? Limite { get; private set; }

    public Task<IReadOnlyList<ProduitResume>> RechercherAsync(string texte, int limite, CancellationToken ct = default)
    {
        (Texte, Limite) = (texte, limite);
        return Task.FromResult<IReadOnlyList<ProduitResume>>([]);
    }
}

internal sealed class ComptesEnMemoire(params Utilisateur[] comptes) : IComptesLecture
{
    public Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default) =>
        Task.FromResult(comptes.FirstOrDefault(c => c.Identifiant == identifiant && c.Actif));

    public List<long> Connexions { get; } = [];

    public Task NoterConnexionAsync(long utilisateurId, CancellationToken ct = default)
    {
        Connexions.Add(utilisateurId);
        return Task.CompletedTask;
    }
}

/// <summary>Le "hash" est simplement le mot de passe préfixé : suffisant pour tester le cas d'usage.</summary>
internal sealed class VerificateurFactice : IVerificateurMotDePasse
{
    public bool Verifier(string motDePasse, string hash) => hash == "hash:" + motDePasse;
}

internal sealed class JetonsFactices : IGenerateurJeton
{
    public (string Jeton, DateTime ExpireLe) Creer(long utilisateurId, string nomComplet) =>
        ($"jeton-{utilisateurId}", new DateTime(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc));
}

internal sealed class TracabiliteEspion : ITracabiliteLecture
{
    public string? CodeRecu { get; private set; }

    public Task<ParcoursBoite?> ParcoursAsync(string codeOuSerie, CancellationToken ct = default)
    {
        CodeRecu = codeOuSerie;
        return Task.FromResult<ParcoursBoite?>(null);
    }
}
