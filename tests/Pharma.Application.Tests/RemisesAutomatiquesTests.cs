using Pharma.Application.Caisse;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Remise automatique : pourcentage entier de 1 à 100 ; une remise négative est refusée avec un message clair, rien n'est enregistré.</summary>
public class RemisesAutomatiquesTests
{
    private sealed class AdminEnMemoire : IRemisesAutomatiquesAdmin
    {
        public int Creees { get; private set; }
        public Task<IReadOnlyList<RemiseAutomatique>> ListerAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<RemiseAutomatique>>([]);
        public Task<RemiseAutomatique?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult<RemiseAutomatique?>(null);
        public Task<ProduitRemise?> ProduitAsync(long produitId, CancellationToken ct = default) => Task.FromResult<ProduitRemise?>(new ProduitRemise("Crème", false));
        public Task<string?> CategorieAsync(string code, CancellationToken ct = default) => Task.FromResult<string?>(code);
        public Task<bool> ChevaucheAsync(RemiseAutomatiqueSaisie s, long? saufId, CancellationToken ct = default) => Task.FromResult(false);
        public Task<long> CreerAsync(RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default) { Creees++; return Task.FromResult(1L); }
        public Task ModifierAsync(long id, RemiseAutomatiqueSaisie s, long auteurId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ArreterAsync(long id, long auteurId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static readonly DateOnly Jour = CatalogueEnMemoire.Aujourdhui;

    private static (GererRemisesAutomatiques Cas, AdminEnMemoire Admin) Cas()
    {
        var admin = new AdminEnMemoire();
        return (new GererRemisesAutomatiques(admin, new DroitsFactices("caisse.remises_auto"), new JournalEspion(), new HorlogeFixe(Jour)), admin);
    }

    [Theory]
    [InlineData(-10, "La remise ne peut pas être négative : saisissez un nombre entier de 1 à 100 %.")]
    [InlineData(0, "La remise est un nombre entier de 1 à 100 %.")]
    [InlineData(101, "La remise est un nombre entier de 1 à 100 %.")]
    [InlineData(12.5, "La remise est un nombre entier de 1 à 100 %.")]
    public async Task Pourcentage_hors_limites_refuse_avec_message(double pct, string message)
    {
        var (cas, admin) = Cas();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.CreerAsync(new RemiseAutomatiqueSaisie(11, null, (decimal)pct, Jour, Jour.AddDays(5), null), 1));
        Assert.Equal(message, ex.Message);
        Assert.Equal("pourcentage_remise", ex.Champ);
        Assert.Equal(0, admin.Creees);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public async Task Pourcentage_de_1_a_100_accepte(double pct)
    {
        var (cas, admin) = Cas();
        await cas.CreerAsync(new RemiseAutomatiqueSaisie(11, null, (decimal)pct, Jour, Jour.AddDays(5), null), 1);
        Assert.Equal(1, admin.Creees);
    }
}
