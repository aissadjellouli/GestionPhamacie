using System.Reflection;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Sauvegardes;
using Pharma.Application.Tracabilite;
using Pharma.Domain.Notifications;

namespace Pharma.Application.Tests;

internal sealed class NotificationsEnMemoire : INotificationsDepot
{
    public ReglagesNotification Reglages { get; set; } = new("smtp.pharmacie.dz", 587, true, "alertes@pharmacie.dz", "alertes", true,
        "COM3", new TimeOnly(21, 0), null, null, true, true);
    public List<Destinataire> Destinataires { get; } = [];
    public List<NouveauMessage> Ajoutes { get; } = [];
    public List<MessageEnFile> File { get; } = [];
    public List<(long Id, string Erreur, DateTime? Prochaine)> Echecs { get; } = [];
    public List<long> Envoyes { get; } = [];
    public DateOnly? RapportNote { get; private set; }

    public Task<ReglagesNotification> ReglagesAsync(CancellationToken ct = default) => Task.FromResult(Reglages);
    public Task EnregistrerReglagesAsync(ModificationReglagesNotification r, bool changer, byte[]? chiffre, CancellationToken ct = default) => Task.CompletedTask;
    public Task<byte[]?> MotDePasseChiffreAsync(CancellationToken ct = default) => Task.FromResult<byte[]?>([1, 2, 3]);
    public Task NoterRapportEnvoyeAsync(DateOnly jour, CancellationToken ct = default) { RapportNote = jour; return Task.CompletedTask; }
    public Task<IReadOnlyList<Destinataire>> DestinatairesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Destinataire>>(Destinataires);
    public Task<long> CreerDestinataireAsync(SaisieDestinataire d, CancellationToken ct = default) => Task.FromResult(99L);
    public Task<bool> ModifierDestinataireAsync(long id, SaisieDestinataire d, CancellationToken ct = default) => Task.FromResult(true);
    public Task<bool> MarquerValideAsync(long id, bool valide, CancellationToken ct = default) => Task.FromResult(true);
    public Task AjouterMessagesAsync(IReadOnlyList<NouveauMessage> messages, CancellationToken ct = default) { Ajoutes.AddRange(messages); return Task.CompletedTask; }
    public Task<IReadOnlyList<MessageEnFile>> MessagesAEnvoyerAsync(DateTime maintenantUtc, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<MessageEnFile>>(File);
    public Task MarquerEnvoyeAsync(long id, DateTime quandUtc, CancellationToken ct = default) { Envoyes.Add(id); return Task.CompletedTask; }
    public Task MarquerEchecAsync(long id, string erreur, DateTime? prochaine, CancellationToken ct = default) { Echecs.Add((id, erreur, prochaine)); return Task.CompletedTask; }
    public Task<IReadOnlyList<MessageEnFile>> FileAsync(int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MessageEnFile>>(File);
}

internal sealed class SecretsFactices : IProtectionSecret
{
    public byte[] Chiffrer(string secret) => [9];
    public string Dechiffrer(byte[] chiffre) => "mot-de-passe";
}

internal sealed class EmailEspion(bool enPanne = false) : IEnvoiEmail
{
    public List<(string Adresse, string Objet)> Envoyes { get; } = [];
    public Task EnvoyerAsync(ConnexionSmtp c, string adresse, string objet, string texte, string? html, CancellationToken ct = default)
    {
        if (enPanne) throw new IOException("Connexion internet indisponible");
        Envoyes.Add((adresse, objet));
        return Task.CompletedTask;
    }
}

internal sealed class SmsEspion : IEnvoiSms
{
    public List<(string Numero, string Texte)> Envoyes { get; } = [];
    public Task EnvoyerAsync(string port, string numero, string texte, CancellationToken ct = default)
    {
        Envoyes.Add((numero, texte));
        return Task.CompletedTask;
    }
}

public class NotificationsApplicationTests
{
    private static Destinataire Dest(long id, bool valide, params TypeNotification[] types) =>
        new(id, $"Destinataire {id}", $"d{id}@pharmacie.dz", "0661000000", true, true, types.ToHashSet(), valide, true);

    private static readonly DateTime Maintenant = new(2026, 11, 8, 20, 0, 0, DateTimeKind.Utc);

    private static MessageEnFile Message(long id, CanalNotification canal, int tentatives = 0) =>
        new(id, canal, "Titulaire", canal == CanalNotification.Email ? "t@p.dz" : "0661000000", TypeNotification.EcartCaisse,
            "Objet", "Corps", null, "en_attente", tentatives, Maintenant, null, Maintenant.AddMinutes(-30), null);

    [Fact]
    public async Task Destinataire_non_verifie_ne_recoit_aucun_message_ordinaire()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, valide: true, TypeNotification.EcartCaisse), Dest(2, valide: false, TypeNotification.EcartCaisse) } };
        var n = await new NotificationService(depot).MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartCaisse, "o", "texte", null, "sms"));
        Assert.Equal(2, n);                                   // e-mail et SMS du seul destinataire vérifié
        Assert.All(depot.Ajoutes, m => Assert.Equal(1, m.DestinataireId));
    }

    [Fact]
    public async Task Pas_de_sms_sans_modem_configure()
    {
        var depot = new NotificationsEnMemoire { Reglages = new NotificationsEnMemoire().Reglages with { ModemPort = null }, Destinataires = { Dest(1, true, TypeNotification.EcartCaisse) } };
        await new NotificationService(depot).MettreEnFileAsync(new ContenuNotification(TypeNotification.EcartCaisse, "o", "texte", null, "sms"));
        Assert.Equal([CanalNotification.Email], depot.Ajoutes.Select(m => m.Canal));
    }

    [Fact]
    public async Task Echec_d_envoi_reprogramme_avec_delai_croissant()
    {
        var depot = new NotificationsEnMemoire { File = { Message(1, CanalNotification.Email, tentatives: 0), Message(2, CanalNotification.Email, tentatives: 3) } };
        var bilan = await new TraiterFileAttente(depot, new EmailEspion(enPanne: true), new SmsEspion(), new SecretsFactices()).ExecuterAsync(Maintenant);
        Assert.Equal(new BilanEnvoi(0, 2), bilan);
        Assert.Equal(Maintenant.AddMinutes(1), depot.Echecs.Single(e => e.Id == 1).Prochaine);
        Assert.Equal(Maintenant.AddMinutes(60), depot.Echecs.Single(e => e.Id == 2).Prochaine);
        Assert.Contains("internet", depot.Echecs[0].Erreur);
    }

    [Fact]
    public async Task Envoi_reussi_par_email_et_sms()
    {
        var depot = new NotificationsEnMemoire { File = { Message(1, CanalNotification.Email), Message(2, CanalNotification.Sms) } };
        var sms = new SmsEspion();
        var bilan = await new TraiterFileAttente(depot, new EmailEspion(), sms, new SecretsFactices()).ExecuterAsync(Maintenant);
        Assert.Equal(new BilanEnvoi(2, 0), bilan);
        Assert.Equal([1L, 2L], depot.Envoyes);
        Assert.Single(sms.Envoyes);
    }

    [Fact]
    public async Task Sans_serveur_smtp_l_email_reste_en_file()
    {
        var depot = new NotificationsEnMemoire { Reglages = new NotificationsEnMemoire().Reglages with { SmtpServeur = null }, File = { Message(1, CanalNotification.Email) } };
        await new TraiterFileAttente(depot, new EmailEspion(), new SmsEspion(), new SecretsFactices()).ExecuterAsync(Maintenant);
        Assert.Contains("SMTP", depot.Echecs.Single().Erreur);
        Assert.NotNull(depot.Echecs.Single().Prochaine);
    }

    private static DonneesRapportQuotidien Donnees() => new(
        new DateOnly(2026, 11, 8), 42, 125_300.50m, 30_000m,
        [new EcartEmploye("Vendeur <Démo>", "Poste caisse 1", -1200m), new EcartEmploye("Assistant Démo", "Poste caisse 2", 0m)],
        2, 700m,
        [new AlerteDuJour("peremption", "Insuline glargine 100 UI/ml", "G7710", "Expire dans 84 jours")],
        [new AlerteDuJour("rupture", "Metformine 850 mg cp (Labo A)", null, "En rupture")]);

    [Fact]
    public void Rapport_quotidien_contient_les_chiffres_du_jour()
    {
        var r = RapportQuotidien.Construire(Donnees(), "Pharmacie de la Recette");
        Assert.Equal(TypeNotification.RapportQuotidien, r.Type);
        Assert.Null(r.TexteSms);                                       // détaillé : e-mail seulement
        Assert.Contains("08/11/2026", r.Objet);
        foreach (var attendu in new[] { "125 300,50 DA", "42 vente(s)", "-1 200,00 DA", "Annulations : 2", "Insuline glargine", "lot G7710", "Metformine" })
            Assert.Contains(attendu, r.TexteEmail.Replace('\u202F', ' ').Replace('\u00A0', ' '));
        Assert.Contains("Vendeur &lt;D", r.HtmlEmail);                // HTML échappé
        Assert.DoesNotContain("<Démo>", r.HtmlEmail);
    }

    [Fact]
    public async Task Rapport_envoye_une_seule_fois_apres_l_heure_reglee()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, true, TypeNotification.RapportQuotidien) } };
        var cas = new EnvoyerRapportQuotidien(depot, new DonneesFactices(Donnees()), new NotificationService(depot), new PharmacieEnMemoire());
        Assert.False(await cas.ExecuterAsync(new DateTime(2026, 11, 8, 20, 59, 0)));
        Assert.True(await cas.ExecuterAsync(new DateTime(2026, 11, 8, 21, 0, 0)));
        Assert.Equal(new DateOnly(2026, 11, 8), depot.RapportNote);
        depot.Reglages = depot.Reglages with { DernierRapport = new DateOnly(2026, 11, 8) };
        Assert.False(await cas.ExecuterAsync(new DateTime(2026, 11, 8, 22, 0, 0)));
    }

    private sealed class DonneesFactices(DonneesRapportQuotidien d) : IDonneesRapport
    {
        public Task<DonneesRapportQuotidien> LireAsync(DateOnly jour, CancellationToken ct = default) => Task.FromResult(d);
    }

    // ---- Confidentialité : aucune donnée de patient ni d'ordonnance ----

    [Fact]
    public void Les_modeles_des_messages_n_ont_aucun_champ_patient_ou_ordonnance()
    {
        Type[] modeles = [typeof(DonneesRapportQuotidien), typeof(EcartEmploye), typeof(AlerteDuJour), typeof(ContenuNotification), typeof(NouveauMessage)];
        string[] interdits = ["patient", "ordonnance", "assure", "medecin", "telephonepatient"];
        foreach (var t in modeles)
            foreach (var p in t.GetProperties())
                Assert.DoesNotContain(interdits, i => p.Name.Contains(i, StringComparison.OrdinalIgnoreCase));

        var parametres = typeof(IAlertesTitulaire).GetMethods().SelectMany(m => m.GetParameters()).Select(p => p.Name!.ToLowerInvariant());
        Assert.DoesNotContain(parametres, n => interdits.Any(n.Contains) || n == "motif");
    }

    [Fact]
    public async Task Rappel_de_lot_notifie_sans_patient_ni_motif()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, true, TypeNotification.LotBloque) } };
        var rappel = new RappelFactice(new ResultatRappel("Insuline glargine", "G7710", 1, 9,
            [new VenteARappeler("Patient Secret", "0770123456", DateTime.UtcNow, "T-000042", 2, false)]));
        await new RappelerLot(rappel, new AlertesTitulaire(depot, new NotificationService(depot)))
            .ExecuterAsync(9, "G7710", "contrefaçon signalée par Patient Secret", 1);

        Assert.Equal(2, depot.Ajoutes.Count);
        foreach (var m in depot.Ajoutes)
        {
            Assert.Contains("G7710", m.Corps);
            Assert.DoesNotContain("Patient Secret", (m.Objet ?? "") + m.Corps + m.CorpsHtml);
            Assert.DoesNotContain("0770123456", m.Corps);
            Assert.DoesNotContain("contrefaçon", m.Corps);
        }
    }

    [Fact]
    public async Task Ecart_de_caisse_sms_seulement_au_dela_du_seuil()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, true, TypeNotification.EcartCaisse) } };
        var alertes = new AlertesTitulaire(depot, new NotificationService(depot));
        await alertes.EcartCaisseAsync("Vendeur Démo", "Caisse 1", -300m, seuilCaisse: 500m);
        Assert.Empty(depot.Ajoutes);
        await alertes.EcartCaisseAsync("Vendeur Démo", "Caisse 1", -1200m, seuilCaisse: 500m);
        Assert.Contains(depot.Ajoutes, m => m.Canal == CanalNotification.Sms && m.Corps.StartsWith("Ecart de caisse -1"));
    }

    // ---- Destinataires : le titulaire seulement ----

    [Fact]
    public async Task Seul_le_titulaire_renseigne_les_destinataires()
    {
        var saisie = new SaisieDestinataire("Titulaire", "t@pharmacie.dz", null, true, false, [TypeNotification.RapportQuotidien], true);
        var droitAdmin = new DroitsFactices("admin.parametres");   // droit donné par la matrice, sans être titulaire
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new EnregistrerDestinataire(new NotificationsEnMemoire(), droitAdmin, new JournalEspion()).ExecuterAsync(null, saisie, 5));
        Assert.Contains("titulaire", ex.Message);

        Assert.Equal(99, await new EnregistrerDestinataire(new NotificationsEnMemoire(), new DroitsFactices { Titulaire = true }, new JournalEspion())
            .ExecuterAsync(null, saisie, 1));
    }

    [Fact]
    public async Task Message_de_test_seulement_vers_un_destinataire_enregistre()
    {
        var depot = new NotificationsEnMemoire { Destinataires = { Dest(1, valide: false, TypeNotification.EcartCaisse) } };
        var test = new EnvoyerMessageTest(depot, new DroitsFactices { Titulaire = true });
        Assert.Equal(2, await test.ExecuterAsync(1, 1));                       // non vérifié : le test sert à le vérifier
        await Assert.ThrowsAsync<Domain.Commun.RegleMetierException>(() => test.ExecuterAsync(42, 1));   // adresse inconnue
    }
}

// ---- Sauvegardes ----

internal sealed class SauvegardesEnMemoire : ISauvegardesDepot
{
    public ReglagesSauvegarde Reglages { get; set; } = new(true, @"E:\Sauvegardes", new TimeOnly(22, 0), 2, null);
    public List<NouvelleEntreeSauvegarde> Journal { get; } = [];
    public Task<ReglagesSauvegarde> ReglagesAsync(CancellationToken ct = default) => Task.FromResult(Reglages);
    public Task EnregistrerReglagesAsync(bool actif, string? dossier, TimeOnly heure, int conservation, CancellationToken ct = default) => Task.CompletedTask;
    public Task NoterExecutionAsync(DateOnly jour, CancellationToken ct = default) { Reglages = Reglages with { DerniereExecution = jour }; return Task.CompletedTask; }
    public Task<long> JournaliserAsync(NouvelleEntreeSauvegarde e, CancellationToken ct = default) { Journal.Add(e); return Task.FromResult((long)Journal.Count); }
    public Task<IReadOnlyList<EntreeSauvegarde>> JournalAsync(int limite, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntreeSauvegarde>>([]);
}

internal sealed class StockageFactice(bool disque = true, bool panne = false) : IStockageSauvegarde
{
    public List<string> Fichiers { get; } = [@"E:\Sauvegardes\pharmacie_20261106.dump", @"E:\Sauvegardes\pharmacie_20261105.dump"];
    public List<string> Supprimes { get; } = [];
    public bool DisqueDisponible(string dossier) => disque;
    public Task<long> ExporterBaseAsync(string fichier, CancellationToken ct = default)
    {
        if (panne) throw new InvalidOperationException("pg_dump a échoué (code 1)");
        Fichiers.Insert(0, fichier);
        return Task.FromResult(5L * 1024 * 1024);
    }
    public IReadOnlyList<string> Sauvegardes(string dossier) => Fichiers.ToList();
    public void Supprimer(string fichier) { Fichiers.Remove(fichier); Supprimes.Add(fichier); }
}

internal sealed class AlertesTitulaireEspion : IAlertesTitulaire
{
    public List<(bool DisqueAbsent, string Detail)> Sauvegardes { get; } = [];
    public Task EcartCaisseAsync(string e, string p, decimal ecart, decimal seuil, CancellationToken ct = default) => Task.CompletedTask;
    public Task LotBloqueAsync(string produit, string lot, int stock, int vendues, CancellationToken ct = default) => Task.CompletedTask;
    public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) { Sauvegardes.Add((disqueAbsent, detail)); return Task.CompletedTask; }
    public List<(string Rayon, int Lignes)> Inventaires { get; } = [];
    public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) { Inventaires.Add((rayon, lignes)); return Task.CompletedTask; }
}

public class SauvegardesTests
{
    private static readonly DateTime Soir = new(2026, 11, 8, 22, 5, 0);

    [Fact]
    public async Task Disque_externe_absent_journalise_et_signale()
    {
        var (depot, alertes) = (new SauvegardesEnMemoire(), new AlertesTitulaireEspion());
        var r = await new LancerSauvegarde(depot, new StockageFactice(disque: false), alertes, new HorlogeFixe(new DateOnly(2026, 11, 8))).ExecuterAsync(true, null, Soir);
        Assert.Equal("disque_absent", r.Statut);
        Assert.Equal("disque_absent", depot.Journal.Single().Statut);
        Assert.True(alertes.Sauvegardes.Single().DisqueAbsent);
    }

    [Fact]
    public async Task Echec_de_pg_dump_signale()
    {
        var (depot, alertes) = (new SauvegardesEnMemoire(), new AlertesTitulaireEspion());
        var r = await new LancerSauvegarde(depot, new StockageFactice(panne: true), alertes, new HorlogeFixe(new DateOnly(2026, 11, 8))).ExecuterAsync(false, 1, Soir);
        Assert.Equal("echouee", r.Statut);
        Assert.Contains("pg_dump", alertes.Sauvegardes.Single().Detail);
    }

    [Fact]
    public async Task Reussite_garde_les_plus_recentes_selon_la_conservation()
    {
        var stockage = new StockageFactice();
        var alertes = new AlertesTitulaireEspion();
        var r = await new LancerSauvegarde(new SauvegardesEnMemoire(), stockage, alertes, new HorlogeFixe(new DateOnly(2026, 11, 8))).ExecuterAsync(true, null, Soir);
        Assert.Equal("reussie", r.Statut);
        Assert.EndsWith("pharmacie_20261108_220500.dump", r.Fichier);
        Assert.Equal([@"E:\Sauvegardes\pharmacie_20261105.dump"], stockage.Supprimes);   // conservation : 2 fichiers
        Assert.Empty(alertes.Sauvegardes);
    }

    [Fact]
    public async Task Automatique_une_fois_par_jour_apres_l_heure()
    {
        var depot = new SauvegardesEnMemoire();
        var lancer = new LancerSauvegarde(depot, new StockageFactice(), new AlertesTitulaireEspion(), new HorlogeFixe(new DateOnly(2026, 11, 8)));
        var auto = new SauvegardeAutomatique(depot, lancer);
        Assert.Null(await auto.ExecuterAsync(Soir.AddHours(-1)));
        Assert.NotNull(await auto.ExecuterAsync(Soir));
        Assert.Null(await auto.ExecuterAsync(Soir.AddMinutes(30)));
    }
}
