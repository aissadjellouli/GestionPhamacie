using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SuperAdmin;
using Pharma.Domain.Commun;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Tests;

internal sealed class CompteSuperAdminEnMemoire(CompteSuperAdmin? compte = null) : ICompteSuperAdmin
{
    public CompteSuperAdmin? Compte { get; private set; } = compte;
    public string Emplacement => "mémoire";
    public CompteSuperAdmin? Lire() => Compte;
    public void Enregistrer(CompteSuperAdmin c) => Compte = c;
}

internal sealed class HorlogeReglable(DateTime maintenant) : IHorloge
{
    public DateTime MaintenantUtc { get; set; } = maintenant;
    public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
}

/// <summary>E-mail qui échoue pour les serveurs donnés (ex. ancien serveur d'essai arrêté), et note les serveurs utilisés.</summary>
internal sealed class EmailParServeur(params string[] enPanne) : IEnvoiEmail
{
    public List<string> Serveurs { get; } = [];
    public Task EnvoyerAsync(ConnexionSmtp c, string adresse, string objet, string texte, string? html, CancellationToken ct = default)
    {
        if (enPanne.Contains(c.Serveur)) throw new IOException("Connexion refusée");
        Serveurs.Add(c.Serveur);
        return Task.CompletedTask;
    }
}

internal sealed class SmsEnPanne : IEnvoiSms
{
    public Task EnvoyerAsync(string port, string numero, string texte, CancellationToken ct = default) => throw new IOException("Modem débranché");
}

/// <summary>Base détruite ou injoignable : toute lecture échoue.</summary>
internal sealed class DepotInjoignable : INotificationsDepot
{
    private static Task<T> Panne<T>() => Task.FromException<T>(new InvalidOperationException("Base injoignable"));
    public Task<ReglagesNotification> ReglagesAsync(CancellationToken ct = default) => Panne<ReglagesNotification>();
    public Task EnregistrerReglagesAsync(ModificationReglagesNotification r, bool c, byte[]? m, CancellationToken ct = default) => Panne<bool>();
    public Task<byte[]?> MotDePasseChiffreAsync(CancellationToken ct = default) => Panne<byte[]?>();
    public Task NoterRapportEnvoyeAsync(DateOnly jour, CancellationToken ct = default) => Panne<bool>();
    public Task<IReadOnlyList<Destinataire>> DestinatairesAsync(CancellationToken ct = default) => Panne<IReadOnlyList<Destinataire>>();
    public Task<long> CreerDestinataireAsync(SaisieDestinataire d, CancellationToken ct = default) => Panne<long>();
    public Task<bool> ModifierDestinataireAsync(long id, SaisieDestinataire d, CancellationToken ct = default) => Panne<bool>();
    public Task<bool> MarquerValideAsync(long id, bool valide, CancellationToken ct = default) => Panne<bool>();
    public Task AjouterMessagesAsync(IReadOnlyList<NouveauMessage> m, CancellationToken ct = default) => Panne<bool>();
    public Task<IReadOnlyList<MessageEnFile>> MessagesAEnvoyerAsync(DateTime m, int l, CancellationToken ct = default) => Panne<IReadOnlyList<MessageEnFile>>();
    public Task MarquerEnvoyeAsync(long id, DateTime q, CancellationToken ct = default) => Panne<bool>();
    public Task MarquerEchecAsync(long id, string e, DateTime? p, CancellationToken ct = default) => Panne<bool>();
    public Task MarquerSignaleAsync(long id, DateTime q, CancellationToken ct = default) => Panne<bool>();
    public Task<IReadOnlyList<MessageEnFile>> FileAsync(int limite, CancellationToken ct = default) => Panne<IReadOnlyList<MessageEnFile>>();
}

public class SuperAdminTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 11, 10, 0, 0, DateTimeKind.Utc);
    private static CompteSuperAdmin Compte(CopieEnvoi? envoi = null) => new("admin.pharma", "hash:Secret-Super-2026", "0661234512", "admin@pharmacie.dz", envoi);

    private static (DemanderValidationSuperAdmin Cas, ValidationsSuperAdmin Etat, SmsEspion Sms, EmailEspion Email) Demandeur(
        IEnvoiSms? sms = null, INotificationsDepot? depot = null, CompteSuperAdmin? compte = null, IHorloge? horloge = null)
    {
        var comptes = new CompteSuperAdminEnMemoire(compte ?? Compte());
        var etat = new ValidationsSuperAdmin(horloge);
        var (espionSms, email) = (new SmsEspion(), new EmailEspion());
        var envoi = new EnvoiCodeSuperAdmin(comptes, sms ?? espionSms, email, new SecretsFactices(), depot ?? new NotificationsEnMemoire());
        var cas = new DemanderValidationSuperAdmin(new SeConnecterSuperAdmin(comptes, new VerificateurFactice(), etat, horloge), etat, envoi,
            new SuperAdminCourant { Identifiant = "admin.pharma" });
        return (cas, etat, espionSms, email);
    }

    private static string CodeDuSms(SmsEspion sms) => System.Text.RegularExpressions.Regex.Match(sms.Envoyes.Single().Texte, @"code (\d{6})").Groups[1].Value;

    [Fact]
    public async Task Code_envoye_par_SMS_puis_valide_une_seule_fois_pour_cette_action()
    {
        var (cas, etat, sms, _) = Demandeur();
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026");

        Assert.Equal(("SMS", "06•••••512"), (d.Canal, d.Destinataire));
        Assert.Equal("0661234512", sms.Envoyes.Single().Numero);
        Assert.DoesNotContain("é", sms.Envoyes.Single().Texte);   // SMS sans accents (jeu GSM)
        var code = CodeDuSms(sms);

        Assert.Throws<RegleMetierException>(() => etat.Verifier(d.Id, code, ActionsSuperAdmin.RepriseExtraire));   // autre action
        var v = etat.Verifier(d.Id, code, ActionsSuperAdmin.RepriseCharger);
        Assert.Equal(ActionsSuperAdmin.RepriseCharger, v.Action);
        Assert.Throws<RegleMetierException>(() => etat.Verifier(d.Id, code, ActionsSuperAdmin.RepriseCharger));   // déjà utilisé
    }

    [Fact]
    public async Task Mot_de_passe_faux_aucun_code_envoye()
    {
        var (cas, _, sms, email) = Demandeur();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "faux"));
        Assert.Equal("mot_de_passe_validation", ex.Champ);
        Assert.Empty(sms.Envoyes);
        Assert.Empty(email.Envoyes);
    }

    [Fact]
    public async Task Code_expire_apres_5_minutes()
    {
        var horloge = new HorlogeReglable(Maintenant);
        var (cas, etat, sms, _) = Demandeur(horloge: horloge);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026");
        horloge.MaintenantUtc = Maintenant.AddMinutes(6);
        var ex = Assert.Throws<RegleMetierException>(() => etat.Verifier(d.Id, CodeDuSms(sms), ActionsSuperAdmin.RepriseCharger));
        Assert.Contains("expiré", ex.Message);
    }

    [Fact]
    public async Task Trois_codes_faux_bloquent_les_validations_15_minutes()
    {
        var horloge = new HorlogeReglable(Maintenant);
        var (cas, etat, _, _) = Demandeur(horloge: horloge);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026");
        for (var i = 0; i < ValidationsSuperAdmin.EssaisMax; i++)
            Assert.Throws<RegleMetierException>(() => etat.Verifier(d.Id, "000000x", ActionsSuperAdmin.RepriseCharger));

        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026"));
        horloge.MaintenantUtc = Maintenant.AddMinutes(16);
        Assert.NotNull(await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026"));
    }

    [Fact]
    public async Task SMS_en_panne_code_envoye_par_email()
    {
        var (cas, _, _, email) = Demandeur(sms: new SmsEnPanne());
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026");
        Assert.Equal(("e-mail", "a•••@pharmacie.dz"), (d.Canal, d.Destinataire));
        Assert.Equal("admin@pharmacie.dz", email.Envoyes.Single().Adresse);
    }

    [Fact]
    public async Task Base_detruite_le_code_part_avec_la_copie_des_reglages()
    {
        // Sans copie : aucun canal, action impossible
        var (sansCopie, _, _, _) = Demandeur(depot: new DepotInjoignable());
        await Assert.ThrowsAsync<RegleMetierException>(() => sansCopie.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026"));

        var copie = new CopieEnvoi("COM4", null, 587, true, null, null, null);
        var (cas, _, sms, _) = Demandeur(depot: new DepotInjoignable(), compte: Compte(copie));
        Assert.Equal("SMS", (await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026")).Canal);
        Assert.Single(sms.Envoyes);
    }

    [Fact]
    public async Task Reglages_lus_en_base_recopies_dans_le_compte()
    {
        var comptes = new CompteSuperAdminEnMemoire(Compte());
        var envoi = new EnvoiCodeSuperAdmin(comptes, new SmsEspion(), new EmailEspion(), new SecretsFactices(), new NotificationsEnMemoire());
        await envoi.EnvoyerAsync(comptes.Compte!, null, "code 123456");
        Assert.Equal(("COM3", "smtp.pharmacie.dz", "mot-de-passe"), (comptes.Compte!.Envoi!.ModemPort, comptes.Compte.Envoi.SmtpServeur, comptes.Compte.Envoi.SmtpMotDePasse));
    }

    [Fact]
    public async Task Base_restauree_aux_reglages_perimes_le_code_part_avec_la_copie_qui_reste_intacte()
    {
        // Base restaurée : ancien serveur e-mail d'essai, injoignable ; la copie du compte a les bons réglages
        var depot = new NotificationsEnMemoire { Reglages = new("127.0.0.1", 2525, false, "alertes@essai.dz", null, false, null, new TimeOnly(21, 0), null, null, true, true) };
        var bonne = new CopieEnvoi(null, "smtp.gmail.com", 587, true, "pharmacie@gmail.com", "pharmacie@gmail.com", "secret");
        var comptes = new CompteSuperAdminEnMemoire(Compte(bonne));
        var email = new EmailParServeur("127.0.0.1");
        var envoi = new EnvoiCodeSuperAdmin(comptes, new SmsEspion(), email, new SecretsFactices(), depot);

        var (canal, _) = await envoi.EnvoyerAsync(comptes.Compte!, null, "code 123456");

        Assert.Equal("e-mail", canal);
        Assert.Equal(["smtp.gmail.com"], email.Serveurs);
        Assert.Equal(bonne, comptes.Compte!.Envoi);   // jamais écrasée par les réglages périmés
    }

    [Fact]
    public async Task Copie_mise_a_jour_seulement_apres_un_envoi_reussi_avec_les_reglages_de_la_base()
    {
        var depot = new NotificationsEnMemoire { Reglages = new("smtp.nouveau.dz", 587, true, "a@nouveau.dz", null, false, null, new TimeOnly(21, 0), null, null, true, true) };
        var ancienne = new CopieEnvoi(null, "smtp.ancien.dz", 587, true, "a@ancien.dz", null, null);
        var comptes = new CompteSuperAdminEnMemoire(Compte(ancienne));
        await new EnvoiCodeSuperAdmin(comptes, new SmsEspion(), new EmailParServeur(), new SecretsFactices(), depot).EnvoyerAsync(comptes.Compte!, null, "code 1");
        Assert.Equal("smtp.nouveau.dz", comptes.Compte!.Envoi!.SmtpServeur);
    }

    [Fact]
    public async Task Nouveau_numero_verifie_par_SMS_seulement()
    {
        var comptes = new CompteSuperAdminEnMemoire(Compte());
        var email = new EmailEspion();
        var envoi = new EnvoiCodeSuperAdmin(comptes, new SmsEnPanne(), email, new SecretsFactices(), new NotificationsEnMemoire());
        await Assert.ThrowsAsync<RegleMetierException>(() => envoi.EnvoyerAsync(comptes.Compte!, "0770000000", "code 123456"));
        Assert.Empty(email.Envoyes);
    }

    [Fact]
    public void Connexion_refusee_puis_bloquee_apres_5_mots_de_passe_faux()
    {
        var connecter = new SeConnecterSuperAdmin(new CompteSuperAdminEnMemoire(Compte()), new VerificateurFactice(), new ValidationsSuperAdmin(), new HorlogeReglable(Maintenant));
        Assert.Null(connecter.Executer("autre", "Secret-Super-2026"));
        Assert.NotNull(connecter.Executer("ADMIN.pharma", "Secret-Super-2026"));
        for (var i = 1; i < 5; i++) Assert.Null(connecter.Executer("admin.pharma", "faux"));
        Assert.Throws<RegleMetierException>(() => connecter.Executer("admin.pharma", "faux"));
        Assert.Throws<RegleMetierException>(() => connecter.Executer("admin.pharma", "Secret-Super-2026"));
    }

    [Theory]
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "", "admin@pharmacie.dz")]          // téléphone obligatoire
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "0213456789", "admin@pharmacie.dz")] // pas un mobile
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "0661234512", "")]                   // e-mail obligatoire
    [InlineData("admin.pharma", "court", "court", "0661234512", "admin@pharmacie.dz")]
    [InlineData("admin.pharma", "Secret-Super-2026", "Autre-Super-2026", "0661234512", "admin@pharmacie.dz")]
    public void Creation_refusee_si_incomplete(string identifiant, string mdp, string confirmation, string telephone, string email)
    {
        var comptes = new CompteSuperAdminEnMemoire();
        Assert.Throws<RegleMetierException>(() => new CreerCompteSuperAdmin(comptes, new HacheurFactice()).Executer(identifiant, mdp, confirmation, telephone, email));
        Assert.Null(comptes.Compte);
    }

    [Fact]
    public void Creation_garde_le_mot_de_passe_hache_et_le_numero_normalise()
    {
        var comptes = new CompteSuperAdminEnMemoire();
        new CreerCompteSuperAdmin(comptes, new HacheurFactice()).Executer(" admin.pharma ", "Secret-Super-2026", "Secret-Super-2026", "0661 23 45 12", "admin@pharmacie.dz");
        Assert.Equal(("admin.pharma", "hash:Secret-Super-2026", "0661234512"), (comptes.Compte!.Identifiant, comptes.Compte.MotDePasseHash, comptes.Compte.Telephone));
    }

    [Fact]
    public void Numero_et_adresse_jamais_affiches_en_entier()
    {
        Assert.Equal("06•••••512", RegleDestinataire.MasquerTelephone("06 61 23 45 12"));
        Assert.Equal("a•••@pharmacie.dz", RegleDestinataire.MasquerEmail("admin@pharmacie.dz"));
    }
}
