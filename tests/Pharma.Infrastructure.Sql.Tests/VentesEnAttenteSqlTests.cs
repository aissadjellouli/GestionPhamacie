using Pharma.Application.Caisse;
using Pharma.Domain.Commun;
using Pharma.Infrastructure.Caisse;
using Xunit.Abstractions;

namespace Pharma.Infrastructure.Sql.Tests;

/// <summary>C7 et C8 : borne de 20 ventes en attente par poste, tenue sous le verrou consultatif du poste.</summary>
[Trait("Categorie", "Sql")]
public sealed class VentesEnAttenteSqlTests(CopieSql copie, ITestOutputHelper sortie) : IClassFixture<CopieSql>
{
    private static string Cle(string poste) => "panier_attente:" + poste;

    private Task<long> EnvoyerAsync(string poste, long utilisateur) =>
        new VentesEnAttenteSql(copie.Source).EnregistrerAsync(poste, utilisateur, "Panier d'essai", 1, 10m, "{}", null, default, GererVentesEnAttente.MaxParPoste);

    [FactSql]
    public async Task C7_deux_envois_simultanes_sur_19_paniers_un_seul_passe()
    {
        sortie.WriteLine("Copie : " + copie.Nom);
        var poste = $"POSTE-ESSAI-{Donnees.Suivant()}";
        var utilisateur = await copie.Donnees.UtilisateurAsync();
        await copie.Donnees.PanierAttenteAsync(poste, utilisateur, 19);
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.VerrouConsultatifAsync(Cle(poste));

        var envoi1 = Task.Run(() => EnvoyerAsync(poste, utilisateur));
        var envoi2 = Task.Run(() => EnvoyerAsync(poste, utilisateur));
        await bloqueur.AttendreBloquesAsync(2);
        Assert.False(envoi1.IsCompleted || envoi2.IsCompleted, "un envoi aurait dû attendre le verrou du poste");
        await bloqueur.ValiderAsync();

        var issues = new List<Exception?>();
        foreach (var envoi in new[] { envoi1, envoi2 })
        {
            try { await envoi.WaitAsync(TimeSpan.FromSeconds(10)); issues.Add(null); }
            catch (RegleMetierException e) { issues.Add(e); }
        }
        Assert.Equal(1, issues.Count(e => e is null));
        var refus = Assert.Single(issues, e => e is not null);
        Assert.Equal(GererVentesEnAttente.MessageTropDAttente, refus!.Message);
        Assert.Equal(20, await copie.Donnees.NombrePaniersAsync(poste));
    }

    [FactSql]
    public async Task C8_le_verrou_d_un_poste_ne_bloque_pas_un_autre_poste()
    {
        var posteA = $"POSTE-ESSAI-A{Donnees.Suivant()}";
        var posteB = $"POSTE-ESSAI-B{Donnees.Suivant()}";
        var utilisateur = await copie.Donnees.UtilisateurAsync();
        await using var bloqueur = await Bloqueur.DemarrerAsync(copie);
        await bloqueur.VerrouConsultatifAsync(Cle(posteA));

        var id = await EnvoyerAsync(posteB, utilisateur).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(id > 0);
        Assert.Equal(1, await copie.Donnees.NombrePaniersAsync(posteB));
        Assert.Equal(0, await copie.Donnees.NombrePaniersAsync(posteA));
        await bloqueur.ValiderAsync();
    }
}
