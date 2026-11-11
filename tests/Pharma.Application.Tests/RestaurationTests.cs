using Microsoft.Extensions.DependencyInjection;
using Pharma.Application.Commun;
using Pharma.Application.Ged;
using Pharma.Application.Sauvegardes;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class RestaurationFactice(bool existe = true) : IRestaurationBase
{
    public TaskCompletionSource Liberer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Bloquer { get; init; }
    public List<string> Restaurees { get; } = [];
    public string NomBase => "pharmacie";
    public Task<bool> BaseExisteAsync(CancellationToken ct = default) => Task.FromResult(existe);
    public Task VerifierAsync(string fichier, CancellationToken ct = default) =>
        fichier.Contains("abime") ? throw new RegleMetierException("Sauvegarde illisible") : Task.CompletedTask;
    public async Task<string?> RestaurerAsync(string fichier, Action<string> etape, CancellationToken ct = default)
    {
        if (Bloquer) await Liberer.Task;
        etape("Base pharmacie restaurée");
        Restaurees.Add(fichier);
        return existe ? "pharmacie_avant_restauration_20261111_1800" : null;
    }
}

internal sealed class TitulaireFactice(long? id = 1) : ITitulaireLecture
{
    public Task<long?> IdAsync(CancellationToken ct = default) => Task.FromResult(id);
}

/// <summary>Conteneur minimal : chaque portée crée ses propres services (comme une requête du serveur).</summary>
internal sealed class ServicesFactices(Func<Type, ServicesFactices.Portee, object?> fabrique) : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new Portee(fabrique);

    internal sealed class Portee(Func<Type, Portee, object?> fabrique) : IServiceScope, IServiceProvider
    {
        private readonly Dictionary<Type, object?> crees = [];
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type type) => crees.TryGetValue(type, out var s) ? s : crees[type] = fabrique(type, this);
        public void Dispose() { }
    }
}

public class RestaurationTests
{
    private sealed record Monde(RestaurationsSuperAdmin Restaurations, RestaurationFactice Restauration, StockageFactice Stockage, JournalEspion Journal, string Ged);

    private static Monde Creer(RestaurationFactice? restauration = null, CompteSuperAdmin? compte = null)
    {
        var (r, stockage, journal) = (restauration ?? new RestaurationFactice(), new StockageFactice(), new JournalEspion());
        // Sauvegardes réelles (la liste ne propose que des fichiers présents)
        var dossier = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "sauvegardes-" + Guid.NewGuid().ToString("N"))).FullName;
        stockage.Fichiers.Clear();
        foreach (var nom in new[] { "pharmacie_20261110_220000.dump", "pharmacie_20261109_220000.dump", "pharmacie_abime.dump" })
        {
            File.WriteAllText(Path.Combine(dossier, nom), "x");
            stockage.Fichiers.Add(Path.Combine(dossier, nom));
        }
        var ged = new ReglagesGed(Path.Combine(Path.GetTempPath(), "ged-restauration-" + Guid.NewGuid().ToString("N")));
        var comptes = new CompteSuperAdminEnMemoire(compte ?? new CompteSuperAdmin("admin.pharma", "hash:x", "0661234512", "a@b.dz"));
        var portees = new ServicesFactices((type, p) => type.Name switch
        {
            nameof(IRestaurationBase) => r,
            nameof(IStockageSauvegarde) => stockage,
            nameof(ReglagesGed) => ged,
            nameof(ICompteSuperAdmin) => comptes,
            nameof(ISauvegardesDepot) => new SauvegardesEnMemoire(),
            nameof(ITitulaireLecture) => new TitulaireFactice(),
            nameof(IJournal) => journal,
            nameof(SuperAdminCourant) => new SuperAdminCourant(),
            nameof(ListerRestaurations) => new ListerRestaurations(r, stockage, ged, comptes, (SuperAdminCourant)p.GetService(typeof(SuperAdminCourant))!, new SauvegardesEnMemoire()),
            _ => null
        });
        return new Monde(new RestaurationsSuperAdmin(portees), r, stockage, journal, ged.Racine);
    }

    private static SuperAdminCourant Valide(string action) => new()
    {
        Identifiant = "admin.pharma", Validation = new ValidationAcceptee(action, "SMS", "06•••••512")
    };

    private static async Task<EtatRestauration> AttendreAsync(RestaurationsSuperAdmin r)
    {
        for (var i = 0; i < 200 && r.Etat.EnCours; i++) await Task.Delay(10);
        return r.Etat;
    }

    [Fact]
    public async Task Restauration_de_la_base_refusee_sans_code_ou_hors_liste()
    {
        var m = Creer();
        await Assert.ThrowsAsync<DroitRefuseException>(() => m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers[0], new SuperAdminCourant { Identifiant = "admin.pharma" }));
        await Assert.ThrowsAsync<DroitRefuseException>(() => m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers[0], Valide(ActionsSuperAdmin.RestaurerGed)));
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Restaurations.RestaurerBaseAsync(@"C:\Windows\autre.dump", Valide(ActionsSuperAdmin.RestaurerBase)));
        Assert.Empty(m.Restauration.Restaurees);
    }

    [Fact]
    public async Task Restauration_de_la_base_copie_de_securite_puis_journal_du_super_admin()
    {
        var m = Creer();
        var fichier = m.Stockage.Fichiers[0];
        await m.Restaurations.RestaurerBaseAsync(fichier, Valide(ActionsSuperAdmin.RestaurerBase));
        var e = await AttendreAsync(m.Restaurations);

        Assert.Null(e.Erreur);
        Assert.Equal([fichier], m.Restauration.Restaurees);
        Assert.Contains("pharmacie_avant_restauration_20261111_1800", e.Resultat);
        Assert.Contains(m.Stockage.Fichiers, f => f.Contains("pharmacie_avant_restauration_"));   // copie de sécurité faite avant
        Assert.Equal(["restauration_base"], m.Journal.Actions);
        Assert.Equal("admin.pharma", m.Journal.Details[0]["super_admin"]);
        Assert.False(m.Restaurations.BaseEnCours);
    }

    [Fact]
    public async Task Base_detruite_restauree_sans_copie_de_securite()
    {
        var m = Creer(new RestaurationFactice(existe: false));
        var avant = m.Stockage.Fichiers.Count;
        await m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers[0], Valide(ActionsSuperAdmin.RestaurerBase));
        var e = await AttendreAsync(m.Restaurations);
        Assert.Null(e.Erreur);
        Assert.Equal(avant, m.Stockage.Fichiers.Count);
        Assert.DoesNotContain("ancienne base", e.Resultat);
    }

    [Fact]
    public async Task Une_restauration_a_la_fois_et_maintenance_pendant_la_base()
    {
        var m = Creer(new RestaurationFactice { Bloquer = true });
        await m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers[0], Valide(ActionsSuperAdmin.RestaurerBase));
        Assert.True(m.Restaurations.BaseEnCours);
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers[0], Valide(ActionsSuperAdmin.RestaurerBase)));
        m.Restauration.Liberer.SetResult();
        await AttendreAsync(m.Restaurations);
        Assert.False(m.Restaurations.BaseEnCours);
    }

    [Fact]
    public async Task Sauvegarde_illisible_rien_n_est_restaure()
    {
        var m = Creer();
        await m.Restaurations.RestaurerBaseAsync(m.Stockage.Fichiers.Single(x => x.Contains("abime")), Valide(ActionsSuperAdmin.RestaurerBase));
        var e = await AttendreAsync(m.Restaurations);
        Assert.Contains("illisible", e.Erreur);
        Assert.Empty(m.Restauration.Restaurees);
    }

    [Fact]
    public async Task GED_refusee_sans_copie_sur_le_disque_externe()
    {
        var m = Creer(compte: new CompteSuperAdmin("admin.pharma", "hash:x", "0661234512", "a@b.dz"));
        await Assert.ThrowsAsync<RegleMetierException>(() => m.Restaurations.RestaurerGedAsync(Valide(ActionsSuperAdmin.RestaurerGed)));
        await Assert.ThrowsAsync<DroitRefuseException>(() => m.Restaurations.RestaurerGedAsync(Valide(ActionsSuperAdmin.RestaurerBase)));
    }
}
