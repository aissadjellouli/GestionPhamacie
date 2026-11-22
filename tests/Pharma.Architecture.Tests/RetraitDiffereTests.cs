using Pharma.Api.Synchronisation;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Client HTTP du central remplacé (renouvellement du certificat) : libéré seulement après un délai, jamais pendant qu'un appel en cours
/// peut encore s'en servir (revue du 2026-11-22 : ObjectDisposedException, erreur 500 côté administration).
/// </summary>
public class RetraitDiffereTests
{
    private sealed class Objet : IDisposable
    {
        public bool Libere;
        public void Dispose() => Libere = true;
    }

    [Fact]
    public void Un_objet_remplace_n_est_libere_qu_apres_le_delai()
    {
        var t0 = new DateTime(2026, 11, 22, 10, 0, 0, DateTimeKind.Utc);
        var retires = new RetraitDiffere<Objet>(TimeSpan.FromMinutes(30));
        var ancien = new Objet();
        retires.Retirer(ancien, t0);
        Assert.Equal(0, retires.Purger(t0.AddMinutes(1)));     // un appel en cours peut encore l'utiliser
        Assert.False(ancien.Libere);
        var second = new Objet();
        retires.Retirer(second, t0.AddMinutes(20));
        Assert.Equal(1, retires.Purger(t0.AddMinutes(30)));
        Assert.True(ancien.Libere);
        Assert.False(second.Libere);
        Assert.Equal(1, retires.EnAttente);
    }

    [Fact]
    public void Le_client_du_central_ne_libere_plus_l_ancien_client_aussitot()
    {
        var code = File.ReadAllText(Path.Combine(Racine(), "src", "Pharma.Api", "Synchronisation", "ClientServeurCentralHttp.cs"));
        Assert.DoesNotContain("ancien.Http.Dispose()", code);
        Assert.Contains("retires.Retirer(", code);
        Assert.True(RetraitDiffere<Objet>.DelaiParDefaut >= TimeSpan.FromMinutes(10));
    }

    private static string Racine()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx"))) dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }
}
