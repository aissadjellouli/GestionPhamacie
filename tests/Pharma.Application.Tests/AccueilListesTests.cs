using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Tests;

/// <summary>Accueil du titulaire (2026-11-15) : écarts de caisse et alertes non envoyées en tableaux paginés et filtrés par le serveur.</summary>
public class AccueilListesTests
{
    private static EcartCaisseRecent Ecart(long id) => new(id, "Poste A", "Vendeur", new DateTime(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc), -100m);

    [Fact]
    public async Task Les_ecarts_de_caisse_sont_lus_par_page_avec_le_texte_nettoye()
    {
        var ecarts = new EcartsCaisseEnMemoire();
        ecarts.Ecarts.AddRange(Enumerable.Range(1, 12).Select(i => Ecart(i)));

        var page2 = await new ConsulterEcartsCaisseRecents(ecarts, new DroitsFactices { Titulaire = true }).ExecuterAsync(1, "  poste a ", new DemandePage(2, 5));

        Assert.Equal(12, page2.Nombre);
        Assert.Equal([6L, 7, 8, 9, 10], page2.Derniers.Select(e => e.SessionId));
        Assert.Equal("poste a", ecarts.Demande!.Value.Texte);
    }

    [Fact]
    public async Task Un_filtre_vide_lit_tous_les_ecarts()
    {
        var ecarts = new EcartsCaisseEnMemoire();
        await new ConsulterEcartsCaisseRecents(ecarts, new DroitsFactices { Titulaire = true }).ExecuterAsync(1, "   ");
        Assert.Null(ecarts.Demande!.Value.Texte);
        Assert.Equal(new DemandePage(), ecarts.Demande!.Value.Page);
    }

    [Fact]
    public async Task Les_alertes_non_envoyees_sont_lues_par_page()
    {
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        for (var i = 0; i < 7; i++) await nonEnvoyees.NoterAsync(TypeNotification.EcartCaisse, $"Alerte {i + 1}", "aucun destinataire");

        var page = await new ConsulterAlertesNonEnvoyees(nonEnvoyees, new DroitsFactices { Titulaire = true }).ExecuterAsync(1, null, new DemandePage(2, 5));

        Assert.Equal(7, page.Nombre);
        Assert.Equal(["Alerte 2", "Alerte 1"], page.Dernieres.Select(a => a.Objet));
    }
}
