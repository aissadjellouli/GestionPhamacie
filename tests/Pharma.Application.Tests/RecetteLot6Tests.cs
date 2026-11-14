using Pharma.Application.Caisse;
using Pharma.Application.Notifications;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Tests;

internal sealed class EcartsCaisseEnMemoire : IEcartsCaisseRecents
{
    public List<EcartCaisseRecent> Ecarts { get; } = [];
    public (long JusquA, long Par)? Marque { get; private set; }
    public Task<EcartsCaisseNonVus> NonVusAsync(int jours, int limite, CancellationToken ct = default) =>
        Task.FromResult(new EcartsCaisseNonVus(Ecarts.Count, Ecarts.Take(limite).ToList()));
    public Task<int> MarquerVusAsync(long jusquA, long utilisateurId, CancellationToken ct = default)
    {
        Marque = (jusquA, utilisateurId);
        return Task.FromResult(Ecarts.Count);
    }
}

public class RecetteLot6Tests
{
    private static readonly DateTime Maintenant = new(2026, 11, 14, 20, 0, 0, DateTimeKind.Utc);

    private static MessageEnFile Message(DateTime creeLe, bool signale = false) =>
        new(7, CanalNotification.Email, "Titulaire", "t@p.dz", TypeNotification.EcartCaisse, "Manque de caisse", "texte", null, "en_attente", 3, null, null,
            creeLe, null, signale);

    // ---- Alertes sans destinataire : option du titulaire ; les pannes toujours signalées ----

    [Fact]
    public async Task Alerte_sans_destinataire_non_signalee_si_le_titulaire_a_desactive_l_option()
    {
        var depot = new NotificationsEnMemoire();
        depot.Reglages = depot.Reglages with { SignalerSansDestinataire = false };
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new AlertesTitulaire(depot, new NotificationService(depot), nonEnvoyees).LotBloqueAsync("Insuline", "G7710", 3, 0);
        Assert.Empty(nonEnvoyees.Notees);

        // Option active (défaut) : signalée
        depot.Reglages = depot.Reglages with { SignalerSansDestinataire = true };
        await new AlertesTitulaire(depot, new NotificationService(depot), nonEnvoyees).LotBloqueAsync("Insuline", "G7710", 3, 0);
        Assert.Equal(AlertesTitulaire.AucunDestinataire, Assert.Single(nonEnvoyees.Notees).Raison);
    }

    [Fact]
    public async Task Panne_de_mise_en_file_toujours_signalee_meme_option_desactivee()
    {
        var depot = new NotificationsEnMemoire();
        depot.Reglages = depot.Reglages with { SignalerSansDestinataire = false };
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new AlertesTitulaire(depot, new NotificationsEnPanne(), nonEnvoyees, new TraceEspion()).SauvegardeEnEchecAsync(true, "Disque E: absent");
        Assert.StartsWith("Mise en file impossible", Assert.Single(nonEnvoyees.Notees).Raison);
    }

    [Fact]
    public void Option_par_defaut_activee() => Assert.True(new NotificationsEnMemoire().Reglages.SignalerSansDestinataire);

    // ---- Rapport du soir parti vers personne ----

    private sealed class RapportVide : IDonneesRapport
    {
        public Task<DonneesRapportQuotidien> LireAsync(DateOnly jour, CancellationToken ct = default) =>
            Task.FromResult(new DonneesRapportQuotidien(jour, 0, 0, 0, [], 0, 0, [], []));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task Rapport_du_soir_sans_aucun_destinataire_signale_selon_l_option(bool option, int signales)
    {
        var depot = new NotificationsEnMemoire();
        depot.Reglages = depot.Reglages with { SignalerSansDestinataire = option };
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        var cas = new EnvoyerRapportQuotidien(depot, new RapportVide(), new NotificationService(depot), new PharmacieEnMemoire(), null, nonEnvoyees);
        Assert.True(await cas.ExecuterAsync(new DateTime(2026, 11, 14, 21, 30, 0)));
        Assert.Equal(signales, nonEnvoyees.Notees.Count);
        if (signales == 1)
            Assert.Equal((TypeNotification.RapportQuotidien, "Rapport du 14/11/2026"), (nonEnvoyees.Notees[0].Type, nonEnvoyees.Notees[0].Objet));
    }

    [Fact]
    public async Task Rapport_du_soir_parti_rien_a_signaler()
    {
        var depot = new NotificationsEnMemoire
        {
            Destinataires = { new Destinataire(1, "Titulaire", "t@p.dz", null, true, false, new HashSet<TypeNotification> { TypeNotification.RapportQuotidien }, true, true) }
        };
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new EnvoyerRapportQuotidien(depot, new RapportVide(), new NotificationService(depot), new PharmacieEnMemoire(), null, nonEnvoyees)
            .ExecuterAsync(new DateTime(2026, 11, 14, 21, 30, 0));
        Assert.Single(depot.Ajoutes);
        Assert.Empty(nonEnvoyees.Notees);
    }

    // ---- Envoi en échec : signalé une fois, après un jour d'essais ; les essais continuent ----

    [Fact]
    public async Task Envoi_en_echec_depuis_un_jour_signale_une_seule_fois_et_reessaye()
    {
        var depot = new NotificationsEnMemoire();
        depot.File.Add(Message(Maintenant.AddHours(-25)));
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        var cas = new TraiterFileAttente(depot, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices(), nonEnvoyees);
        await cas.ExecuterAsync(Maintenant);
        Assert.NotNull(Assert.Single(depot.Echecs).Prochaine);                 // pas abandonné : nouvel essai prévu
        Assert.StartsWith("Envoi en échec depuis plus d’un jour", Assert.Single(nonEnvoyees.Notees).Raison);
        Assert.Equal([7L], depot.Signales);

        // Déjà signalé : plus rien au bandeau aux essais suivants
        depot.File[0] = Message(Maintenant.AddHours(-25), signale: true);
        await cas.ExecuterAsync(Maintenant.AddHours(1));
        Assert.Single(nonEnvoyees.Notees);
    }

    [Fact]
    public async Task Envoi_abandonne_a_sept_jours_deja_signale_apres_un_jour_pas_de_second_signalement()
    {
        // Signalé au bout d'un jour ; à l'abandon (7 jours), le message passe en échec sans nouveau signalement au bandeau
        var depot = new NotificationsEnMemoire();
        depot.File.Add(Message(Maintenant.AddDays(-7).AddMinutes(-1), signale: true));
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new TraiterFileAttente(depot, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices(), nonEnvoyees).ExecuterAsync(Maintenant);
        Assert.Null(Assert.Single(depot.Echecs).Prochaine);                    // abandonné
        Assert.Empty(nonEnvoyees.Notees);
        Assert.Empty(depot.Signales);

        // Jamais signalé (serveur arrêté plusieurs jours) : l'abandon est signalé, une fois
        var autre = new NotificationsEnMemoire();
        autre.File.Add(Message(Maintenant.AddDays(-7).AddMinutes(-1)));
        var signalees = new AlertesNonEnvoyeesEnMemoire();
        await new TraiterFileAttente(autre, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices(), signalees).ExecuterAsync(Maintenant);
        Assert.StartsWith("Envoi abandonné", Assert.Single(signalees.Notees).Raison);
        Assert.Equal([7L], autre.Signales);
    }

    [Fact]
    public async Task Envoi_en_echec_depuis_moins_d_un_jour_pas_encore_signale()
    {
        var depot = new NotificationsEnMemoire();
        depot.File.Add(Message(Maintenant.AddHours(-23)));
        var nonEnvoyees = new AlertesNonEnvoyeesEnMemoire();
        await new TraiterFileAttente(depot, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices(), nonEnvoyees).ExecuterAsync(Maintenant);
        Assert.Empty(nonEnvoyees.Notees);
        Assert.Empty(depot.Signales);
    }

    [Theory]
    [InlineData(23, false, false, false)]
    [InlineData(24, false, false, true)]
    [InlineData(48, true, false, false)]   // déjà signalé
    [InlineData(2, false, true, true)]     // abandon jamais signalé avant
    [InlineData(170, true, true, false)]   // abandon déjà signalé après un jour : pas de second signalement
    public void Signalement_d_un_envoi_en_echec(int heures, bool dejaSignale, bool abandon, bool attendu) =>
        Assert.Equal(attendu, RegleNouvelleTentative.ASignaler(Maintenant, Maintenant.AddHours(-heures), dejaSignale, abandon));

    [Fact]
    public void Duree_des_essais_inchangee() => Assert.Equal(TimeSpan.FromDays(7), RegleNouvelleTentative.DureeMaximale);

    // ---- Écarts de caisse récents : titulaire seul ----

    [Fact]
    public async Task Ecarts_de_caisse_recents_reserves_au_titulaire_et_marques_vus()
    {
        var ecarts = new EcartsCaisseEnMemoire { Ecarts = { new EcartCaisseRecent(12, "Caisse 1", "Vendeur", Maintenant, -300m) } };
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ConsulterEcartsCaisseRecents(ecarts, new DroitsFactices("caisse.voir_ecart")).ExecuterAsync(2));
        await Assert.ThrowsAsync<DroitRefuseException>(() => new MarquerEcartsCaisseVus(ecarts, new DroitsFactices("caisse.voir_ecart")).ExecuterAsync(12, 2));
        Assert.Null(ecarts.Marque);

        var titulaire = new DroitsFactices { Titulaire = true };
        var vus = await new ConsulterEcartsCaisseRecents(ecarts, titulaire).ExecuterAsync(1);
        Assert.Equal((1, "Caisse 1", -300m), (vus.Nombre, vus.Derniers[0].Poste, vus.Derniers[0].Ecart));
        await Assert.ThrowsAsync<RegleMetierException>(() => new MarquerEcartsCaisseVus(ecarts, titulaire).ExecuterAsync(0, 1));
        await new MarquerEcartsCaisseVus(ecarts, titulaire).ExecuterAsync(12, 1);
        Assert.Equal((12L, 1L), ecarts.Marque);
    }
}
