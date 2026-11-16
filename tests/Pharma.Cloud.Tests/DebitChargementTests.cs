using Pharma.Cloud.Api.Reception;

namespace Pharma.Cloud.Tests;

/// <summary>Limite de dépôts relevée pendant un chargement complet (script central 006).</summary>
public class DebitChargementTests
{
    private sealed class Horloge(DateTimeOffset t) : TimeProvider
    {
        public DateTimeOffset T = t;
        public override DateTimeOffset GetUtcNow() => T;
    }

    [Fact]
    public void Seau_et_limite_propres_au_chargement()
    {
        Assert.Equal(("cert:a", 60), SuiviChargementCentral.Politique("cert:a", false, 60, 600));
        Assert.Equal(("cert:a|chargement", 600), SuiviChargementCentral.Politique("cert:a", true, 60, 600));
    }

    [Fact]
    public async Task Etat_lu_en_base_garde_quelques_secondes_et_efface_au_debut_ou_a_la_fin()
    {
        var h = new Horloge(new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero));
        var (lectures, enCours) = (0, true);
        var s = new SuiviChargementCentral((_, _) => { lectures++; return Task.FromResult(enCours); }, h);
        Assert.True(await s.EnCoursAsync("e1", default));
        enCours = false;
        Assert.True(await s.EnCoursAsync("e1", default)); // gardé
        Assert.Equal(1, lectures);
        s.Oublier("e1");                                   // fin du chargement : relu aussitôt
        Assert.False(await s.EnCoursAsync("e1", default));
        enCours = true;
        h.T += SuiviChargementCentral.Duree;               // expiré : relu
        Assert.True(await s.EnCoursAsync("e1", default));
        Assert.Equal(3, lectures);
    }
}
