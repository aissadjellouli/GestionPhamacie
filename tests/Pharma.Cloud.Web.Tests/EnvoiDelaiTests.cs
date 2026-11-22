using Pharma.Cloud.Web.Codes;
using Pharma.Shared;

namespace Pharma.Cloud.Web.Tests;

/// <summary>
/// 2026-11-22 : un envoi (code, identifiant) ne dépend plus de la requête du navigateur ; il a son propre délai, et un délai dépassé
/// est un « echec » ordinaire (noté), jamais une annulation qui interrompt la requête.
/// </summary>
public class EnvoiDelaiTests
{
    private static readonly DestinataireCode Destinataire = new("compte.delai", "d@exemple.invalid", null);

    /// <summary>Serveur d'e-mail qui ne répond jamais : attend jusqu'à l'annulation du jeton reçu.</summary>
    private sealed class EnvoiSansReponse : IEnvoiCodeDistant
    {
        public string Canal => "email";
        public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    private sealed class EnvoiImmediat : IEnvoiCodeDistant
    {
        public int Envois;
        public string Canal => "email";
        public Task EnvoyerAsync(DestinataireCode destinataire, string code, CancellationToken ct)
        {
            Interlocked.Increment(ref Envois);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Delai_propre_de_30_secondes()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), EnvoisCodes.DelaiMaximum);
        Assert.Equal(TimeSpan.FromSeconds(30), TransmissionComptes.DelaiParEnvoi);
    }

    [Fact]
    public async Task Delai_depasse_rend_un_echec_sans_exception()
    {
        using var delai = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var r = await EnvoisCodes.EnvoyerAsync(new EnvoiSansReponse(), Destinataire, "123456", delai.Token);
        Assert.Equal("email:echec", Assert.Single(r).Objet);
    }

    [Fact]
    public async Task Navigateur_qui_abandonne_n_annule_pas_un_envoi_deja_lance_avec_son_propre_jeton()
    {
        // La requête est abandonnée (jeton annulé) ; l'envoi utilise son propre jeton : il part quand même
        using var requete = new CancellationTokenSource();
        await requete.CancelAsync();
        var envoi = new EnvoiImmediat();
        using var propre = new CancellationTokenSource(EnvoisCodes.DelaiMaximum);
        var r = await EnvoisCodes.EnvoyerAsync(envoi, Destinataire, "123456", propre.Token);
        Assert.Equal("email:ok", Assert.Single(r).Objet);
        Assert.Equal(1, envoi.Envois);
        Assert.True(requete.IsCancellationRequested);
    }

    [Fact]
    public async Task Transmission_un_envoi_trop_long_est_un_echec_les_autres_partent()
    {
        var comptes = new[]
        {
            new CompteATransmettre(Guid.NewGuid(), "lent.delai", "titulaire", "lent@exemple.test", null, "Provisoire-1"),
            new CompteATransmettre(Guid.NewGuid(), "rapide.delai", "titulaire", "rapide@exemple.test", null, "Provisoire-2"),
        };
        var partis = new List<string>();
        Task Courriel(string adresse, string objet, string texte, CancellationToken ct)
        {
            if (adresse.StartsWith("lent", StringComparison.Ordinal)) return Task.Delay(Timeout.Infinite, ct);
            lock (partis) partis.Add(adresse);
            Assert.DoesNotContain("Provisoire-", texte);   // jamais le mot de passe dans l'e-mail
            return Task.CompletedTask;
        }
        var r = await TransmissionComptes.TransmettreAsync(comptes, true, Courriel, new EnvoiSmsMotDePasseIndisponible(), null, false, null,
            CancellationToken.None, TimeSpan.FromMilliseconds(100));
        Assert.Equal(["echec", "ok"], r.Select(t => t.Email));
        Assert.Equal(["rapide@exemple.test"], partis);
    }
}
