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

/// <summary>E-mail qui note adresse et texte (doublure : aucun envoi réel), ou échoue.</summary>
internal sealed class EmailTexte(bool enPanne = false) : IEnvoiEmail
{
    public List<(string Adresse, string Texte)> Envoyes { get; } = [];
    public Task EnvoyerAsync(ConnexionSmtp c, string adresse, string objet, string texte, string? html, CancellationToken ct = default)
    {
        if (enPanne) throw new IOException("Serveur SMTP injoignable");
        Envoyes.Add((adresse, texte));
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
        IEnvoiSms? sms = null, INotificationsDepot? depot = null, CompteSuperAdmin? compte = null, IHorloge? horloge = null, IEnvoiEmail? courriel = null)
    {
        var comptes = new CompteSuperAdminEnMemoire(compte ?? Compte());
        var etat = new ValidationsSuperAdmin(horloge);
        var (espionSms, email) = (new SmsEspion(), new EmailEspion());
        var envoi = new EnvoiCodeSuperAdmin(comptes, sms ?? espionSms, courriel ?? email, new SecretsFactices(), depot ?? new NotificationsEnMemoire());
        var cas = new DemanderValidationSuperAdmin(new SeConnecterSuperAdmin(comptes, new VerificateurFactice(), etat, horloge), etat, envoi,
            new SuperAdminCourant { Identifiant = "admin.pharma" });
        return (cas, etat, espionSms, email);
    }

    private static string CodeDans(string texte) => System.Text.RegularExpressions.Regex.Match(texte, @"code (\d{6})").Groups[1].Value;
    private static string CodeDuSms(SmsEspion sms) => System.Text.RegularExpressions.Regex.Match(sms.Envoyes.Single().Texte, @"code (\d{6})").Groups[1].Value;

    [Fact]
    public async Task Code_envoye_par_SMS_puis_valide_une_seule_fois_pour_cette_action()
    {
        var (cas, etat, sms, _) = Demandeur();
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026");

        Assert.Equal(("SMS et e-mail", "06•••••512 et a•••@pharmacie.dz"), (d.Canal, d.Destinataire));
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
        Assert.Equal("Code envoyé par e-mail à a•••@pharmacie.dz ; SMS non parti", d.Message);
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
        Assert.Equal("SMS", (await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, "Secret-Super-2026")).Canal);   // copie sans SMTP : SMS seul
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

        var resultat = await envoi.EnvoyerAsync(comptes.Compte!, null, "code 123456");

        Assert.Equal("e-mail", resultat.Canal);
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
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "", "")]                              // au moins un canal
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "0213456789", "admin@pharmacie.dz")] // pas un mobile
    [InlineData("admin.pharma", "Secret-Super-2026", "Secret-Super-2026", "0661234512", "pas-un-email")]       // e-mail invalide
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
    public void Creation_avec_SMS_seul_ou_email_seul_ou_les_deux()
    {
        var hacheur = new HacheurFactice();
        var sms = new CompteSuperAdminEnMemoire();
        new CreerCompteSuperAdmin(sms, hacheur).Executer("admin.pharma", MotDePasse, MotDePasse, "0661234512", "");
        Assert.Equal(("0661234512", null, 1), (sms.Compte!.Telephone, sms.Compte.Email, sms.Compte.NombreCanaux));

        var mail = new CompteSuperAdminEnMemoire();
        new CreerCompteSuperAdmin(mail, hacheur).Executer("admin.pharma", MotDePasse, MotDePasse, "", " admin@pharmacie.dz ");
        Assert.Equal((null, "admin@pharmacie.dz", 1), (mail.Compte!.Telephone, mail.Compte.Email, mail.Compte.NombreCanaux));

        var deux = new CompteSuperAdminEnMemoire();
        new CreerCompteSuperAdmin(deux, hacheur).Executer("admin.pharma", MotDePasse, MotDePasse, "0661234512", "admin@pharmacie.dz");
        Assert.Equal(2, deux.Compte!.NombreCanaux);
    }

    [Fact]
    public async Task Creation_refusee_quand_aucun_canal_ne_fonctionne_et_rien_n_est_enregistre()
    {
        // Modem et serveur e-mail en panne : aucun code ne part, donc aucun canal n'est vérifié
        var comptes = new CompteSuperAdminEnMemoire();
        var envoi = new EnvoiCodeSuperAdmin(comptes, new SmsEnPanne(), new EmailTexte(enPanne: true), new SecretsFactices(), new NotificationsEnMemoire());
        var provisoire = new CompteSuperAdmin("admin.pharma", "", "0661234512", null);
        await Assert.ThrowsAsync<RegleMetierException>(() => envoi.EnvoyerAsync(provisoire, "0661234512", "code 123456"));
        await Assert.ThrowsAsync<RegleMetierException>(() => envoi.EnvoyerAAdresseAsync(provisoire, "admin@pharmacie.dz", "code 123456"));

        var souhaites = CanauxSuperAdmin.Souhaites(null, "0661234512", "admin@pharmacie.dz");
        var ex = Assert.Throws<RegleMetierException>(() => CanauxSuperAdmin.Retenir(null, souhaites, telephoneVerifie: false, emailVerifie: false));
        Assert.Contains("modem GSM", ex.Message);
        Assert.Contains("serveur e-mail", ex.Message);
        Assert.Contains("Administration › Notifications", ex.Message);
        Assert.Null(comptes.Compte);
    }

    [Fact]
    public void Canal_saisi_mais_non_verifie_n_est_pas_enregistre()
    {
        var souhaites = CanauxSuperAdmin.Souhaites(null, "0661234512", "admin@pharmacie.dz");
        Assert.Equal((null, "admin@pharmacie.dz"), CanauxSuperAdmin.Retenir(null, souhaites, telephoneVerifie: false, emailVerifie: true));
        Assert.Equal(("0661234512", null), CanauxSuperAdmin.Retenir(null, souhaites, telephoneVerifie: true, emailVerifie: false));
    }

    [Fact]
    public void Modification_le_dernier_canal_verifie_ne_se_retire_pas()
    {
        var seulSms = new CompteSuperAdmin("admin.pharma", "h", "0661234512", null);
        var ex = Assert.Throws<RegleMetierException>(() => CanauxSuperAdmin.Souhaites(seulSms, CanauxSuperAdmin.Retrait, ""));
        Assert.Contains("dernier canal", ex.Message);

        // Retrait du SMS pendant que la nouvelle adresse n'est pas vérifiée : le compte resterait sans canal
        var souhaites = CanauxSuperAdmin.Souhaites(seulSms, CanauxSuperAdmin.Retrait, "nouveau@pharmacie.dz");
        Assert.Throws<RegleMetierException>(() => CanauxSuperAdmin.Retenir(seulSms, souhaites, false, emailVerifie: false));
        Assert.Equal((null, "nouveau@pharmacie.dz"), CanauxSuperAdmin.Retenir(seulSms, souhaites, false, emailVerifie: true));

        // Avec deux canaux, on peut en retirer un
        var deux = Compte();
        Assert.Equal(("0661234512", null), CanauxSuperAdmin.Souhaites(deux, "", CanauxSuperAdmin.Retrait));
    }

    [Fact]
    public void Modification_nouveau_numero_ou_nouvelle_adresse_a_verifier_inchange_non()
    {
        var compte = Compte();
        Assert.False(CanauxSuperAdmin.ADeVerifier(compte.Telephone, CanauxSuperAdmin.Souhaites(compte, "06 61 23 45 12", "").Telephone));
        Assert.True(CanauxSuperAdmin.ADeVerifier(compte.Telephone, CanauxSuperAdmin.Souhaites(compte, "0770000000", "").Telephone));
        Assert.True(CanauxSuperAdmin.ADeVerifier(compte.Email, CanauxSuperAdmin.Souhaites(compte, "", "autre@pharmacie.dz").Email));
        // Nouveau numéro non vérifié : l'ancien reste
        var souhaites = CanauxSuperAdmin.Souhaites(compte, "0770000000", "");
        Assert.Equal(("0661234512", "admin@pharmacie.dz"), CanauxSuperAdmin.Retenir(compte, souhaites, telephoneVerifie: false, emailVerifie: false));
    }

    [Fact]
    public void Compte_existant_a_un_seul_canal_ou_ancien_format_reste_lisible()
    {
        var json = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        // Ancien fichier : téléphone et e-mail renseignés
        var ancien = System.Text.Json.JsonSerializer.Deserialize<CompteSuperAdmin>(
            """{"identifiant":"a.b","motDePasseHash":"h","telephone":"0661234512","email":"a@b.dz","envoi":null,"dossierSauvegardes":"E:\\S"}""", json)!;
        Assert.Equal((2, "E:\\S"), (ancien.NombreCanaux, ancien.DossierSauvegardes));

        // Fichier à un seul canal : champ absent ou null
        var seul = System.Text.Json.JsonSerializer.Deserialize<CompteSuperAdmin>("""{"identifiant":"a.b","motDePasseHash":"h","telephone":"0661234512"}""", json)!;
        Assert.Equal((true, false, null), (seul.ASms, seul.AEmail, seul.Email));

        // Les propriétés calculées ne sont pas écrites dans le fichier
        var ecrit = System.Text.Json.JsonSerializer.Serialize(seul, json);
        Assert.DoesNotContain("nombreCanaux", ecrit);
        Assert.Equal(seul, System.Text.Json.JsonSerializer.Deserialize<CompteSuperAdmin>(ecrit, json));
    }

    [Fact]
    public async Task Compte_a_un_seul_canal_le_code_ne_part_que_sur_ce_canal_et_le_message_le_dit()
    {
        var compte = new CompteSuperAdmin("admin.pharma", "hash:" + MotDePasse, null, "admin@pharmacie.dz");
        var courriel = new EmailTexte();
        var (cas, etat, sms, _) = Demandeur(compte: compte, courriel: courriel);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RestaurerBase, MotDePasse);

        Assert.Empty(sms.Envoyes);   // aucun SMS : le compte n'a pas de téléphone
        Assert.Equal("Code envoyé par e-mail à a•••@pharmacie.dz (seul canal vérifié du compte)", d.Message);
        Assert.NotNull(etat.Verifier(d.Id, CodeDans(courriel.Envoyes.Single().Texte), ActionsSuperAdmin.RestaurerBase));

        var parSms = new CompteSuperAdmin("admin.pharma", "hash:" + MotDePasse, "0661234512", null);
        var (cas2, _, sms2, email2) = Demandeur(compte: parSms);
        var d2 = await cas2.ExecuterAsync(ActionsSuperAdmin.RestaurerBase, MotDePasse);
        Assert.Single(sms2.Envoyes);
        Assert.Empty(email2.Envoyes);
        Assert.Equal("Code envoyé par SMS au 06•••••512 (seul canal vérifié du compte)", d2.Message);
    }

    [Fact]
    public async Task Compte_a_un_seul_canal_en_panne_l_action_est_refusee()
    {
        var compte = new CompteSuperAdmin("admin.pharma", "hash:" + MotDePasse, "0661234512", null);
        var (cas, _, _, email) = Demandeur(sms: new SmsEnPanne(), compte: compte);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(ActionsSuperAdmin.RestaurerBase, MotDePasse));
        Assert.Contains("Modem débranché", ex.Message);
        Assert.Empty(email.Envoyes);   // pas de repli vers un canal que le compte n'a pas
    }

    [Fact]
    public void Numero_et_adresse_jamais_affiches_en_entier()
    {
        Assert.Equal("06•••••512", RegleDestinataire.MasquerTelephone("06 61 23 45 12"));
        Assert.Equal("a•••@pharmacie.dz", RegleDestinataire.MasquerEmail("admin@pharmacie.dz"));
    }

    private const string MotDePasse = "Secret-Super-2026";

    [Fact]
    public async Task Le_meme_code_part_par_SMS_et_par_email_et_l_un_ou_l_autre_le_valide()
    {
        var courriel = new EmailTexte();
        var (cas, etat, sms, _) = Demandeur(courriel: courriel);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RestaurerBase, MotDePasse);

        var parSms = CodeDans(sms.Envoyes.Single().Texte);
        var parEmail = CodeDans(courriel.Envoyes.Single().Texte);
        Assert.Matches(@"^\d{6}$", parSms);
        Assert.Equal(parSms, parEmail);
        Assert.Equal("Code envoyé par SMS au 06•••••512 et par e-mail à a•••@pharmacie.dz", d.Message);
        Assert.Equal("admin@pharmacie.dz", courriel.Envoyes.Single().Adresse);
        Assert.Equal(ActionsSuperAdmin.RestaurerBase, etat.Verifier(d.Id, parEmail, ActionsSuperAdmin.RestaurerBase).Action);   // reçu par e-mail
        Assert.Throws<RegleMetierException>(() => etat.Verifier(d.Id, parSms, ActionsSuperAdmin.RestaurerBase));                 // usage unique, même code
    }

    [Fact]
    public async Task Canal_SMS_en_echec_l_email_suffit_et_le_message_le_dit()
    {
        var courriel = new EmailTexte();
        var (cas, etat, _, _) = Demandeur(sms: new SmsEnPanne(), courriel: courriel);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RestaurerGed, MotDePasse);
        Assert.Equal("Code envoyé par e-mail à a•••@pharmacie.dz ; SMS non parti", d.Message);
        Assert.NotNull(etat.Verifier(d.Id, CodeDans(courriel.Envoyes.Single().Texte), ActionsSuperAdmin.RestaurerGed));
    }

    [Fact]
    public async Task Canal_email_en_echec_le_SMS_suffit_et_le_message_le_dit()
    {
        var (cas, etat, sms, _) = Demandeur(courriel: new EmailTexte(enPanne: true));
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseExtraire, MotDePasse);
        Assert.Equal("Code envoyé par SMS au 06•••••512 ; e-mail non parti", d.Message);
        Assert.Equal("SMS", d.Canal);
        Assert.NotNull(etat.Verifier(d.Id, CodeDuSms(sms), ActionsSuperAdmin.RepriseExtraire));
    }

    [Fact]
    public async Task Deux_canaux_en_echec_l_action_est_refusee_sans_rien_enregistrer()
    {
        var (cas, etat, _, _) = Demandeur(sms: new SmsEnPanne(), courriel: new EmailTexte(enPanne: true));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(ActionsSuperAdmin.RestaurerBase, MotDePasse));
        Assert.Contains("ni par SMS ni par e-mail", ex.Message);
        Assert.Contains("Modem débranché", ex.Message);
        Assert.Contains("Serveur SMTP injoignable", ex.Message);
        Assert.Throws<RegleMetierException>(() => etat.Verifier(Guid.NewGuid(), "123456", ActionsSuperAdmin.RestaurerBase));
    }

    [Fact]
    public async Task Journal_des_actions_note_les_canaux_reellement_utilises_jamais_le_code()
    {
        var courriel = new EmailTexte();
        var (cas, etat, sms, _) = Demandeur(sms: new SmsEnPanne(), courriel: courriel);
        var d = await cas.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, MotDePasse);
        var code = CodeDans(courriel.Envoyes.Single().Texte);
        var courant = new SuperAdminCourant { Identifiant = "admin.pharma", Validation = etat.Verifier(d.Id, code, ActionsSuperAdmin.RepriseCharger) };

        var trace = courant.Trace();
        Assert.Equal("code envoyé par e-mail à a•••@pharmacie.dz", trace["validation"]);
        Assert.DoesNotContain(code, string.Join(" ", trace.Values.Select(v => v?.ToString())));
        Assert.DoesNotContain("0661234512", string.Join(" ", trace.Values.Select(v => v?.ToString())));

        // Les deux canaux : le journal les cite tous les deux
        var (cas2, etat2, sms2, _) = Demandeur(courriel: new EmailTexte());
        var d2 = await cas2.ExecuterAsync(ActionsSuperAdmin.RepriseCharger, MotDePasse);
        var courant2 = new SuperAdminCourant { Identifiant = "admin.pharma", Validation = etat2.Verifier(d2.Id, CodeDuSms(sms2), ActionsSuperAdmin.RepriseCharger) };
        Assert.Equal("code envoyé par SMS au 06•••••512 et par e-mail à a•••@pharmacie.dz", courant2.Trace()["validation"]);
    }

    [Fact]
    public async Task Nouvelle_adresse_email_verifiee_par_un_code_envoye_a_cette_adresse_seulement()
    {
        var comptes = new CompteSuperAdminEnMemoire(Compte());
        var (sms, courriel) = (new SmsEspion(), new EmailTexte());
        var envoi = new EnvoiCodeSuperAdmin(comptes, sms, courriel, new SecretsFactices(), new NotificationsEnMemoire());

        var r = await envoi.EnvoyerAAdresseAsync(comptes.Compte!, "nouveau@pharmacie.dz", "PharmaOfficine : code 654321 pour verifier l'e-mail");

        Assert.Equal(("nouveau@pharmacie.dz", "PharmaOfficine : code 654321 pour verifier l'e-mail"), courriel.Envoyes.Single());
        Assert.Empty(sms.Envoyes);   // ni l'ancienne adresse ni le téléphone ne reçoivent ce code
        Assert.Equal(("e-mail", "n•••@pharmacie.dz"), (r.Canal, r.Destinataire));
    }

    [Fact]
    public async Task Nouvelle_adresse_non_joignable_verification_refusee()
    {
        var comptes = new CompteSuperAdminEnMemoire(Compte());
        var sms = new SmsEspion();
        var envoi = new EnvoiCodeSuperAdmin(comptes, sms, new EmailTexte(enPanne: true), new SecretsFactices(), new NotificationsEnMemoire());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => envoi.EnvoyerAAdresseAsync(comptes.Compte!, "nouveau@pharmacie.dz", "code 123456"));
        Assert.Contains("e-mail", ex.Message);
        Assert.Empty(sms.Envoyes);   // pas de repli par SMS : l'adresse doit prouver qu'elle reçoit
    }
}
