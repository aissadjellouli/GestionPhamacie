using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>Synchronisation, lot S3 : bandeau des postes, alertes au titulaire (une par heure), réglages, purge de la file (sans base ni réseau).</summary>
public class SupervisionSynchronisationTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

    private sealed class Horloge(DateTime debut) : IHorloge
    {
        public DateTime MaintenantUtc { get; private set; } = debut;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
        public void Avancer(TimeSpan d) => MaintenantUtc += d;
    }

    private sealed class Depot : ISupervisionSyncDepot
    {
        public EtatSupervisionSync Etat = UnEtat();
        public readonly List<(string Nature, DateTime Quand)> Alertes = [];
        public ReglagesSupervisionSync? Enregistres;
        public int Lectures;
        public Task<EtatSupervisionSync> LireAsync(CancellationToken ct = default) { Lectures++; return Task.FromResult(Etat); }
        public Task NoterAlerteAsync(string nature, DateTime quandUtc, CancellationToken ct = default)
        {
            Alertes.Add((nature, quandUtc));
            Etat = Etat with { DernieresAlertes = new Dictionary<string, DateTime>(Etat.DernieresAlertes) { [nature] = quandUtc } };
            return Task.CompletedTask;
        }
        public Task EnregistrerReglagesAsync(ReglagesSupervisionSync reglages, CancellationToken ct = default) { Enregistres = reglages; return Task.CompletedTask; }
        public readonly List<long> IntegriteSignalee = [];
        public Task NoterIntegriteSignaleeAsync(long derniereAnomalieId, CancellationToken ct = default)
        {
            IntegriteSignalee.Add(derniereAnomalieId);
            Etat = Etat with { IntegriteSignalee = Math.Max(Etat.IntegriteSignalee, derniereAnomalieId) };
            return Task.CompletedTask;
        }
    }

    private static EtatSupervisionSync UnEtat(bool active = true, AttenteSync? attente = null, DateTime? erreurDepuis = null, string? nature = null,
        string? derniere = null, bool lectureSeule = false, ReglagesSupervisionSync? reglages = null) =>
        new(active, lectureSeule, attente ?? new AttenteSync(null, 0), erreurDepuis, nature, derniere, new Dictionary<string, DateTime>(), reglages ?? ReglagesSupervisionSync.Defaut);

    private sealed class Alertes : IAlertesTitulaire
    {
        public readonly List<(string Objet, string Detail, string Sms)> Envoyees = [];
        public bool Panne;
        public Task SynchronisationAsync(string objet, string detail, string sms, CancellationToken ct = default)
        {
            if (Panne) throw new InvalidOperationException("file indisponible");
            Envoyees.Add((objet, detail, sms));
            return Task.CompletedTask;
        }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Certificat(DateTime? echeance) : ICertificatSync
    {
        public CertificatSyncInfo? Lire() => echeance is { } e ? new("ab".PadRight(64, '0'), e, "cd".PadRight(64, '0')) : null;
        public string NouvelleDemande() => "CSR";
        public void Installer(string certificatPem, string autoritePem, string? epinglage) { }
        public X509Certificate2? CertificatClient() => null;
        public X509Certificate2? Autorite() => null;
    }

    private static CertificatSyncInfo Cert(TimeSpan reste) => new("ab".PadRight(64, '0'), Maintenant + reste, "cd".PadRight(64, '0'));

    // ---- Valeurs de départ et bornes ----

    [Fact]
    public void Valeurs_de_depart_bandeau_1_h_retard_24_h_erreur_1_h_purge_30_jours()
    {
        var d = ReglagesSupervisionSync.Defaut;
        Assert.Equal((60, 24, 60, true, 30), (d.BandeauMinutes, d.RetardHeures, d.ErreurMinutes, d.PurgeActive, d.PurgeJours));
        d.Verifier();
    }

    [Theory]
    [InlineData(0, 24, 60, 30, "bandeau_minutes")]
    [InlineData(10_081, 24, 60, 30, "bandeau_minutes")]
    [InlineData(60, 0, 60, 30, "retard_heures")]
    [InlineData(60, 721, 60, 30, "retard_heures")]
    [InlineData(60, 24, 0, 30, "erreur_minutes")]
    [InlineData(60, 24, 60, 0, "purge_jours")]
    [InlineData(60, 24, 60, 366, "purge_jours")]
    public void Reglages_hors_bornes_refuses(int bandeau, int retard, int erreur, int jours, string champ)
    {
        var e = Assert.Throws<RegleMetierException>(() => new ReglagesSupervisionSync(bandeau, retard, erreur, true, jours).Verifier());
        Assert.Equal(champ, e.Champ);
    }

    [Fact]
    public async Task Reglage_enregistre_journalise_et_memoire_oubliee()
    {
        var (depot, journal) = (new Depot(), new JournalEspion());
        var memoire = new MemoireSupervisionSync(new Horloge(Maintenant));
        await memoire.LireAsync(depot, default);
        var nouveaux = new ReglagesSupervisionSync(30, 12, 45, false, 60);
        await new ReglerSupervisionSync(depot, memoire, journal).ExecuterAsync(nouveaux, 5);
        Assert.Equal(nouveaux, depot.Enregistres);
        Assert.Equal(["reglages_supervision_synchronisation"], journal.Actions);
        var avant = depot.Lectures;
        await memoire.LireAsync(depot, default);
        Assert.Equal(avant + 1, depot.Lectures);   // mémoire oubliée : nouvelle lecture
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSupervisionSync(depot, memoire, journal).ExecuterAsync(nouveaux with { PurgeJours = 0 }, 5));
    }

    // ---- Bandeau ----

    [Fact]
    public void Bandeau_seulement_si_activee_au_dela_du_seuil_et_avec_des_operations()
    {
        var depuis = Maintenant.AddMinutes(-61);
        var attente = new AttenteSync(depuis, 42);
        Assert.Equal(new BandeauSync(true, depuis, 42), ConsulterAttenteSync.Decider(UnEtat(attente: attente), Maintenant));
        Assert.False(ConsulterAttenteSync.Decider(UnEtat(attente: new AttenteSync(Maintenant.AddMinutes(-59), 42)), Maintenant).Affiche);   // sous le seuil de 1 h
        Assert.True(ConsulterAttenteSync.Decider(UnEtat(attente: new AttenteSync(Maintenant.AddMinutes(-60), 1)), Maintenant).Affiche);     // seuil atteint
        Assert.False(ConsulterAttenteSync.Decider(UnEtat(active: false, attente: attente), Maintenant).Affiche);                          // désactivée
        Assert.False(ConsulterAttenteSync.Decider(UnEtat(lectureSeule: true, attente: attente), Maintenant).Affiche);                     // 2e serveur en veille
        Assert.False(ConsulterAttenteSync.Decider(UnEtat(attente: new AttenteSync(null, 0)), Maintenant).Affiche);                        // rien en attente
        // Seuil réglé à 10 minutes
        var court = UnEtat(attente: new AttenteSync(Maintenant.AddMinutes(-11), 3), reglages: ReglagesSupervisionSync.Defaut with { BandeauMinutes = 10 });
        Assert.True(ConsulterAttenteSync.Decider(court, Maintenant).Affiche);
    }

    [Fact]
    public async Task Bandeau_reserve_au_titulaire_et_aux_roles_ayant_le_droit_jamais_bloquant()
    {
        var depot = new Depot { Etat = UnEtat(attente: new AttenteSync(Maintenant.AddHours(-3), 120)) };
        var (memoire, horloge) = (new MemoireSupervisionSync(new Horloge(Maintenant)), new Horloge(Maintenant));
        BandeauSync Lire(DroitsFactices droits) => new ConsulterAttenteSync(depot, memoire, droits, horloge).ExecuterAsync(7).GetAwaiter().GetResult();

        Assert.True(Lire(new DroitsFactices { Titulaire = true }).Affiche);
        Assert.True(Lire(new DroitsFactices(Pharma.Domain.Securite.Permissions.SynchronisationSuivre)).Affiche);
        Assert.False(Lire(new DroitsFactices("caisse.vendre")).Affiche);

        // Base illisible : pas de bandeau, aucune exception
        var panne = new ConsulterAttenteSync(new DepotEnPanne(), new MemoireSupervisionSync(horloge), new DroitsFactices { Titulaire = true }, horloge);
        Assert.Equal(BandeauSync.Aucun, await panne.ExecuterAsync(7));
    }

    private sealed class DepotEnPanne : ISupervisionSyncDepot
    {
        public Task<EtatSupervisionSync> LireAsync(CancellationToken ct = default) => throw new InvalidOperationException("base indisponible");
        public Task NoterAlerteAsync(string nature, DateTime quandUtc, CancellationToken ct = default) => Task.CompletedTask;
        public Task EnregistrerReglagesAsync(ReglagesSupervisionSync reglages, CancellationToken ct = default) => Task.CompletedTask;
        public Task NoterIntegriteSignaleeAsync(long derniereAnomalieId, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Etat_memorise_30_secondes_pour_ne_pas_interroger_la_base_a_chaque_poste()
    {
        var (depot, horloge) = (new Depot(), new Horloge(Maintenant));
        var memoire = new MemoireSupervisionSync(horloge);
        await memoire.LireAsync(depot, default);
        horloge.Avancer(TimeSpan.FromSeconds(20));
        await memoire.LireAsync(depot, default);
        Assert.Equal(1, depot.Lectures);
        horloge.Avancer(TimeSpan.FromSeconds(11));
        await memoire.LireAsync(depot, default);
        Assert.Equal(2, depot.Lectures);
    }

    // ---- Alertes : constat ----

    [Fact]
    public void Retard_au_dela_du_seuil_de_24_h_seulement()
    {
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat(attente: new AttenteSync(Maintenant.AddHours(-23), 5)), null, Maintenant));
        var a = SurveillerSynchronisation.Constater(UnEtat(attente: new AttenteSync(Maintenant.AddHours(-24), 5)), null, Maintenant).Single();
        Assert.Equal(NaturesAlerteSync.Retard, a.Nature);
        Assert.Contains("5 opération", a.Detail);
        Assert.DoesNotContain("é", a.Sms);   // SMS sans accents
        // Seuil réglé à 2 h
        var r = UnEtat(attente: new AttenteSync(Maintenant.AddHours(-3), 5), reglages: ReglagesSupervisionSync.Defaut with { RetardHeures = 2 });
        Assert.Single(SurveillerSynchronisation.Constater(r, null, Maintenant));
    }

    [Fact]
    public void Erreur_persistante_seulement_apres_le_seuil_et_le_dernier_message_en_courriel()
    {
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat(erreurDepuis: Maintenant.AddMinutes(-59), nature: NaturesErreurSync.Autre), null, Maintenant));
        var a = SurveillerSynchronisation.Constater(UnEtat(erreurDepuis: Maintenant.AddMinutes(-90), nature: NaturesErreurSync.Autre, derniere: "Serveur central injoignable"), null, Maintenant).Single();
        Assert.Equal(NaturesAlerteSync.Erreur, a.Nature);
        Assert.Contains("Serveur central injoignable", a.Detail);
        Assert.DoesNotContain("injoignable", a.Sms);
    }

    [Fact]
    public void Quota_refuse_alerte_a_part_sans_attendre_le_seuil_d_erreur_et_sans_doublon_d_erreur()
    {
        var a = SurveillerSynchronisation.Constater(UnEtat(erreurDepuis: Maintenant.AddMinutes(-2), nature: NaturesErreurSync.Quota), null, Maintenant).Single();
        Assert.Equal(NaturesAlerteSync.Quota, a.Nature);
        var natures = SurveillerSynchronisation.Constater(UnEtat(erreurDepuis: Maintenant.AddHours(-5), nature: NaturesErreurSync.Quota), null, Maintenant).Select(x => x.Nature);
        Assert.DoesNotContain(NaturesAlerteSync.Erreur, natures);
    }

    [Fact]
    public void Certificat_a_moins_de_30_jours_de_l_echeance_ou_echu()
    {
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat(), Cert(TimeSpan.FromDays(31)), Maintenant));
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat(), null, Maintenant));
        var proche = SurveillerSynchronisation.Constater(UnEtat(), Cert(TimeSpan.FromDays(29)), Maintenant).Single();
        Assert.Equal(NaturesAlerteSync.Certificat, proche.Nature);
        Assert.Contains("proche", proche.Objet);
        var echu = SurveillerSynchronisation.Constater(UnEtat(), Cert(TimeSpan.FromDays(-1)), Maintenant).Single();
        Assert.Contains("échu", echu.Objet);
        // Le renouvellement automatique démarre à 60 jours : l'alerte à 30 jours signale un renouvellement qui n'a pas abouti
        Assert.True(EnvoyerLotsSync.RenouvelerAvant > SurveillerSynchronisation.DelaiCertificat);
    }

    [Fact]
    public void Aucune_donnee_de_patient_dans_les_messages()
    {
        var e = UnEtat(attente: new AttenteSync(Maintenant.AddDays(-3), 99), erreurDepuis: Maintenant.AddHours(-3), nature: NaturesErreurSync.Autre, derniere: "x");
        foreach (var a in SurveillerSynchronisation.Constater(e, Cert(TimeSpan.FromDays(3)), Maintenant))
            Assert.DoesNotMatch("(?i)patient|nom|ordonnance", a.Sms);
    }

    // ---- Alertes : au plus une fois par heure ----

    [Fact]
    public async Task Au_plus_une_alerte_par_heure_et_par_sujet_tant_que_le_probleme_dure()
    {
        var (depot, alertes, horloge) = (new Depot { Etat = UnEtat(attente: new AttenteSync(Maintenant.AddDays(-2), 10)) }, new Alertes(), new Horloge(Maintenant));
        var surveillance = new SurveillerSynchronisation(depot, new Certificat(null), alertes, horloge);

        Assert.Equal([NaturesAlerteSync.Retard], await surveillance.ExecuterAsync());
        Assert.Single(alertes.Envoyees);
        horloge.Avancer(TimeSpan.FromMinutes(1));
        Assert.Empty(await surveillance.ExecuterAsync());
        horloge.Avancer(TimeSpan.FromMinutes(58));
        Assert.Empty(await surveillance.ExecuterAsync());               // 59 minutes
        horloge.Avancer(TimeSpan.FromMinutes(1));
        Assert.Equal([NaturesAlerteSync.Retard], await surveillance.ExecuterAsync());   // 1 h : rappel
        Assert.Equal(2, alertes.Envoyees.Count);

        // Un autre sujet n'est pas retenu par le délai du premier
        depot.Etat = depot.Etat with { ErreurDepuisLe = horloge.MaintenantUtc.AddHours(-2), ErreurNature = NaturesErreurSync.Quota };
        Assert.Equal([NaturesAlerteSync.Quota], await surveillance.ExecuterAsync());
    }

    private sealed class SansPackG : Pharma.Application.Offre.IPacksOffre
    {
        public Task<bool> InclusAsync(string pack, CancellationToken ct = default) => Task.FromResult(pack != Pharma.Application.Offre.PacksOffre.MultiOfficines);
        public Task<int> ScannersMobilesAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task Sans_le_pack_G_ni_retard_ni_erreur_ni_bandeau_la_synchronisation_est_arretee_volontairement()
    {
        // Décision du titulaire du 2026-11-27 : sans G, pas d'alerte « en retard » toutes les heures sans fin, pas de bandeau sur les postes
        var (depot, alertes, horloge) = (new Depot { Etat = UnEtat(attente: new AttenteSync(Maintenant.AddDays(-20), 500), erreurDepuis: Maintenant.AddDays(-2),
            nature: NaturesErreurSync.Autre) }, new Alertes(), new Horloge(Maintenant));
        Assert.Empty(await new SurveillerSynchronisation(depot, new Certificat(Maintenant.AddDays(10)), alertes, horloge, packs: new SansPackG()).ExecuterAsync());
        Assert.Empty(alertes.Envoyees);
        var bandeau = await new ConsulterAttenteSync(depot, new MemoireSupervisionSync(horloge), new DroitsFactices { Titulaire = true }, horloge, packs: new SansPackG())
            .ExecuterAsync(7);
        Assert.False(bandeau.Affiche);
        // Avec le pack G (aucune offre transmise) : le retard est signalé comme avant
        Assert.Contains(NaturesAlerteSync.Retard, await new SurveillerSynchronisation(depot, new Certificat(null), alertes, horloge).ExecuterAsync());
    }

    [Fact]
    public async Task Rien_si_desactivee_ou_en_lecture_seule()
    {
        var attente = new AttenteSync(Maintenant.AddDays(-2), 10);
        foreach (var etat in new[] { UnEtat(active: false, attente: attente), UnEtat(lectureSeule: true, attente: attente) })
        {
            var (depot, alertes) = (new Depot { Etat = etat }, new Alertes());
            Assert.Empty(await new SurveillerSynchronisation(depot, new Certificat(Maintenant.AddDays(-1)), alertes, new Horloge(Maintenant)).ExecuterAsync());
            Assert.Empty(alertes.Envoyees);
            Assert.Empty(depot.Alertes);
        }
    }

    [Fact]
    public async Task Alerte_impossible_a_mettre_en_file_ne_fait_pas_echouer_la_surveillance_et_ne_se_repete_pas_a_la_minute()
    {
        var (depot, alertes, horloge) = (new Depot { Etat = UnEtat(attente: new AttenteSync(Maintenant.AddDays(-2), 10)) }, new Alertes { Panne = true }, new Horloge(Maintenant));
        var surveillance = new SurveillerSynchronisation(depot, new Certificat(null), alertes, horloge);
        Assert.Single(await surveillance.ExecuterAsync());               // la panne est avalée (signalée par l'alerte non envoyée de l'accueil, ailleurs)
        horloge.Avancer(TimeSpan.FromMinutes(1));
        Assert.Empty(await surveillance.ExecuterAsync());
    }

    // ---- Alerte au titulaire : mise en file ----

    [Fact]
    public void Le_sujet_synchronisation_est_abonnable_et_a_son_code()
    {
        Assert.Contains(Pharma.Domain.Notifications.TypeNotification.Synchronisation, Pharma.Domain.Notifications.CodesNotification.Abonnables);
        Assert.Equal("synchronisation", Pharma.Domain.Notifications.CodesNotification.Code(Pharma.Domain.Notifications.TypeNotification.Synchronisation));
        Assert.Equal(Pharma.Domain.Notifications.TypeNotification.Synchronisation, Pharma.Domain.Notifications.CodesNotification.TypeDepuisCode("synchronisation"));
    }

    // ---- Purge ----

    private sealed class DepotPurge : IPurgeSyncDepot
    {
        public EtatPurgeSync Etat = new(true, 30, null, 0, null, 0, 0, null, null, 0, false);
        public readonly List<(int Jours, int MaxLots)> Appels = [];
        public int LotsRestants = 100;
        public Exception? Panne;
        public readonly List<string?> Erreurs = [];
        public Task<EtatPurgeSync> EtatAsync(CancellationToken ct = default) => Task.FromResult(Etat);
        public Task<ResultatPurgeSync> PurgerAsync(int delaiJours, int maxLots, CancellationToken ct = default)
        {
            if (Panne is not null) throw Panne;
            Appels.Add((delaiJours, maxLots));
            var n = Math.Min(maxLots, LotsRestants);
            LotsRestants -= n;
            return Task.FromResult(new ResultatPurgeSync(n, n * 500L, n * 100_000L, n));
        }
        public Task NoterErreurAsync(string? erreur, CancellationToken ct = default) { Erreurs.Add(erreur); return Task.CompletedTask; }
    }

    [Fact]
    public async Task Purge_par_paquets_avec_le_delai_regle_et_bornee_par_passage()
    {
        var depot = new DepotPurge { Etat = new DepotPurge().Etat with { DelaiJours = 45 } };
        var r = await new PurgerSortieSync(depot, new VerrouSync()).ExecuterAsync();
        Assert.Equal(PurgerSortieSync.LotsMaxParPassage, r!.Lots);
        Assert.All(depot.Appels, a => Assert.Equal(45, a.Jours));
        Assert.All(depot.Appels, a => Assert.True(a.MaxLots <= PurgerSortieSync.LotsParLot));
        Assert.Equal([null], depot.Erreurs);
    }

    [Fact]
    public async Task Purge_s_arrete_quand_il_n_y_a_plus_rien_a_purger()
    {
        var depot = new DepotPurge { LotsRestants = 5 };
        var r = await new PurgerSortieSync(depot, new VerrouSync()).ExecuterAsync();
        Assert.Equal(5, r!.Lots);
        Assert.Single(depot.Appels);
    }

    [Fact]
    public async Task Purge_desactivee_ou_base_en_lecture_seule_ou_envoi_en_cours_ne_fait_rien()
    {
        var depot = new DepotPurge { Etat = new DepotPurge().Etat with { Active = false } };
        Assert.Null(await new PurgerSortieSync(depot, new VerrouSync()).ExecuterAsync());
        depot.Etat = depot.Etat with { Active = true, BaseEnLectureSeule = true };
        Assert.Null(await new PurgerSortieSync(depot, new VerrouSync()).ExecuterAsync());
        depot.Etat = depot.Etat with { BaseEnLectureSeule = false };
        var verrou = new VerrouSync();
        Assert.True(verrou.Prendre());                                   // un passage d'envoi tient le verrou
        Assert.Null(await new PurgerSortieSync(depot, verrou).ExecuterAsync());
        Assert.Empty(depot.Appels);
        verrou.Rendre();
        Assert.NotNull(await new PurgerSortieSync(depot, verrou).ExecuterAsync());
        Assert.True(verrou.Prendre());                                   // verrou rendu après la purge
    }

    [Fact]
    public async Task Purge_en_erreur_memorise_l_erreur_sans_donnee_rend_le_verrou_et_relance()
    {
        var depot = new DepotPurge { Panne = new InvalidOperationException("détail technique avec données") };
        var verrou = new VerrouSync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PurgerSortieSync(depot, verrou).ExecuterAsync());
        Assert.Single(depot.Erreurs);
        Assert.DoesNotContain("données", depot.Erreurs[0]);
        Assert.True(verrou.Prendre());
    }

    // ---- Lot S4 : vérification d'intégrité du serveur central ----

    private const string ResumeCentral = """
        {"format":"integrite-v1","statut":"anomalie","verifie_le":"2026-11-17T10:00:00+00:00","complete_le":null,
         "chaines":[{"journal":"journal_vente","cle":"officine","mode":"complet","statut":"rupture","dernier_rang":12,"non_verifiables":2}],
         "sceaux":{"ok":3,"attente":1,"incoherents":0,"non_verifiables":0},"tickets_verifies_jusqu_a":5939,
         "anomalies_total":2,"derniere_anomalie_id":41,
         "anomalies":[{"id":41,"nature":"ligne_modifiee","journal":"journal_vente","rang":13,"ligne":"13","detail":"empreinte différente","detectee_le":"2026-11-17T10:00:00+00:00"},
                      {"id":40,"nature":"ticket_manquant","journal":"journal_vente","rang":null,"ligne":"T-000004","detail":"ticket jamais reçu","detectee_le":"2026-11-16T10:00:00+00:00"}]}
        """;

    [Fact]
    public void Resume_du_serveur_central_lu_et_format_inconnu_ignore()
    {
        var r = IntegriteCentrale.Lire(ResumeCentral)!;
        Assert.Equal((IntegriteCentrale.StatutAnomalie, 2L, 41L, 3L, 1L, 5939L), (r.Statut, r.AnomaliesTotal, r.DerniereAnomalieId, r.SceauxOk, r.SceauxAttente, r.TicketsVerifiesJusquA ?? 0));
        var c = Assert.Single(r.Chaines);
        Assert.Equal(("journal_vente", "complet", "rupture", 12L, 2L), (c.Journal, c.Mode, c.Statut, c.DernierRang, c.NonVerifiables));
        Assert.Equal([41L, 40L], r.Anomalies.Select(a => a.Id));
        Assert.Null(r.Anomalies[1].Rang);
        Assert.Equal(new DateTime(2026, 11, 17, 10, 0, 0, DateTimeKind.Utc), r.VerifieLeUtc);
        Assert.Null(IntegriteCentrale.Lire(null));
        Assert.Null(IntegriteCentrale.Lire("{\"format\":\"autre\"}"));
        Assert.Null(IntegriteCentrale.Lire("pas du json"));
    }

    [Fact]
    public async Task Nouvelle_anomalie_du_serveur_central_signalee_une_fois_puis_plus_jusqu_a_la_suivante()
    {
        var integrite = IntegriteCentrale.Lire(ResumeCentral)!;
        var (depot, alertes, horloge) = (new Depot { Etat = UnEtat() with { Integrite = integrite, IntegriteSignalee = 39 } }, new Alertes(), new Horloge(Maintenant));
        var surveillance = new SurveillerSynchronisation(depot, new Certificat(null), alertes, horloge);

        Assert.Equal([NaturesAlerteSync.Integrite], await surveillance.ExecuterAsync());
        var a = Assert.Single(alertes.Envoyees);
        Assert.Contains("2 nouvelle(s) anomalie(s)", a.Detail);
        Assert.Contains("Ligne modifiée", a.Detail);
        Assert.Contains("ne rien corriger", a.Detail);
        Assert.Equal([41L], depot.IntegriteSignalee);
        Assert.Empty(depot.Alertes);                                      // pas de délai d'une heure pour ce sujet

        horloge.Avancer(TimeSpan.FromHours(3));
        Assert.Empty(await surveillance.ExecuterAsync());                 // déjà signalée : rien, même plus tard
        depot.Etat = depot.Etat with { Integrite = integrite with { DerniereAnomalieId = 42 } };
        Assert.Equal([NaturesAlerteSync.Integrite], await surveillance.ExecuterAsync());   // nouvelle anomalie : nouvelle alerte
        Assert.Equal([41L, 42L], depot.IntegriteSignalee);
    }

    [Fact]
    public void Pas_d_alerte_d_integrite_sans_resume_ni_anomalie()
    {
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat(), null, Maintenant));
        var sans = IntegriteCentrale.Lire(ResumeCentral)! with { DerniereAnomalieId = 0, AnomaliesTotal = 0, Anomalies = [] };
        Assert.Empty(SurveillerSynchronisation.Constater(UnEtat() with { Integrite = sans }, null, Maintenant));
    }
}
