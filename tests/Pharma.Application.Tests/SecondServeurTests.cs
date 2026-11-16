using System.Security.Cryptography;
using System.Text.Json;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>2e serveur (2026-11-16, script 122) : clé de scellement exportée / importée, refus de démarrer en lecture seule, réglages par serveur, alerte de retard.</summary>
public class SecondServeurTests
{
    private const string MotDePasse = "Coffre-Officine-2026";

    // ---- Clé de scellement : export / import ----

    [Fact]
    public async Task Export_puis_import_redonne_la_meme_cle_sur_le_2e_serveur()
    {
        var principal = new CleMemoire(RandomNumberGenerator.GetBytes(32));
        var f = await new ExporterCleScellement(principal, new JournalEspion(), new Horloge()).ExecuterAsync(MotDePasse, MotDePasse, 1);
        var second = new CleMemoire(null);

        var r = new ImporterCleScellement(second).Executer(f.Contenu, MotDePasse, remplacer: false);

        Assert.Equal(principal.Cle, second.Cle);
        Assert.Equal(CoffreCle.Empreinte(principal.Cle!), r.Empreinte);
        Assert.Equal(f.Empreinte, r.Empreinte);
        Assert.False(r.DejaPresente);
        Assert.EndsWith(".pocle", f.NomFichier);
    }

    [Fact]
    public async Task Mauvais_mot_de_passe_refuse_et_rien_n_est_ecrit()
    {
        var principal = new CleMemoire(RandomNumberGenerator.GetBytes(32));
        var f = await new ExporterCleScellement(principal, new JournalEspion(), new Horloge()).ExecuterAsync(MotDePasse, MotDePasse, 1);
        var second = new CleMemoire(null);

        var ex = Assert.Throws<RegleMetierException>(() => new ImporterCleScellement(second).Executer(f.Contenu, "Mauvais-mot-de-passe", false));

        Assert.Contains("Mot de passe incorrect", ex.Message);
        Assert.Null(second.Cle);
    }

    [Fact]
    public void Fichier_altere_refuse()
    {
        var fichier = CoffreCle.Chiffrer(RandomNumberGenerator.GetBytes(32), MotDePasse);
        fichier[^1] ^= 0x01;
        Assert.Throws<RegleMetierException>(() => CoffreCle.Dechiffrer(fichier, MotDePasse));
        Assert.Throws<RegleMetierException>(() => CoffreCle.Dechiffrer([1, 2, 3], MotDePasse));
    }

    [Theory]
    [InlineData("court", "court")]
    [InlineData("Coffre-Officine-2026", "Coffre-Officine-2027")]
    [InlineData("", "")]
    public async Task Mot_de_passe_trop_court_ou_non_confirme_refuse(string motDePasse, string confirmation)
    {
        var journal = new JournalEspion();
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ExporterCleScellement(new CleMemoire(RandomNumberGenerator.GetBytes(32)), journal, new Horloge()).ExecuterAsync(motDePasse, confirmation, 1));
        Assert.Empty(journal.Entrees);
    }

    [Fact]
    public async Task La_cle_et_le_mot_de_passe_ne_sont_jamais_journalises()
    {
        var principal = new CleMemoire(RandomNumberGenerator.GetBytes(32));
        var journal = new JournalEspion();
        var f = await new ExporterCleScellement(principal, journal, new Horloge()).ExecuterAsync(MotDePasse, MotDePasse, 7);
        var import = new ImporterCleScellement(new CleMemoire(null)).Executer(f.Contenu, MotDePasse, false);

        var (action, details) = Assert.Single(journal.Entrees);
        Assert.Equal(ExporterCleScellement.Action, action);
        var texte = JsonSerializer.Serialize(details) + import.Message + import.ToString();
        foreach (var forme in Formes(principal.Cle!))
            Assert.DoesNotContain(forme, texte, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(MotDePasse, texte);
        Assert.Equal(f.Empreinte, details["empreinte"]);
        Assert.Contains(f.Empreinte, import.Message);
        // Le fichier exporté ne contient pas la clé en clair
        Assert.False(f.Contenu.AsSpan().IndexOf(principal.Cle) >= 0);
    }

    [Fact]
    public async Task Une_autre_cle_presente_n_est_remplacee_que_sur_demande()
    {
        var f = await new ExporterCleScellement(new CleMemoire(RandomNumberGenerator.GetBytes(32)), new JournalEspion(), new Horloge())
            .ExecuterAsync(MotDePasse, MotDePasse, 1);
        var ancienne = RandomNumberGenerator.GetBytes(32);
        var second = new CleMemoire((byte[])ancienne.Clone());

        Assert.Throws<RegleMetierException>(() => new ImporterCleScellement(second).Executer(f.Contenu, MotDePasse, remplacer: false));
        Assert.Equal(ancienne, second.Cle);

        var r = new ImporterCleScellement(second).Executer(f.Contenu, MotDePasse, remplacer: true);
        Assert.Equal(f.Empreinte, r.Empreinte);
        Assert.Equal(f.Empreinte, CoffreCle.Empreinte(second.Cle!));

        // Même clé déjà présente : rien à faire
        Assert.True(new ImporterCleScellement(second).Executer(f.Contenu, MotDePasse, false).DejaPresente);
    }

    // ---- Jamais deux API qui écrivent ----

    [Fact]
    public void L_API_refuse_de_demarrer_sur_une_base_en_lecture_seule()
    {
        var refus = ReplicationOfficine.RefusDemarrage(baseEnRecovery: true, null, null);
        Assert.NotNull(refus);
        Assert.Contains("lecture seule", refus);
        Assert.Contains("basculer-second-serveur.ps1", refus);
        Assert.Null(ReplicationOfficine.RefusDemarrage(false, null, null));
        Assert.Null(ReplicationOfficine.RefusDemarrage(null, null, null));   // base injoignable : pas de refus ici
    }

    [Fact]
    public void Deux_bases_principales_l_ancienne_refuse_la_plus_recemment_promue_demarre()
    {
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(false, true, "SRV-2", timeline: 1, timelineAutre: 2));   // ancien principal revenu
        Assert.Null(ReplicationOfficine.RefusDemarrage(false, true, "SRV-1", timeline: 2, timelineAutre: 1));      // 2e serveur promu
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(false, true, "SRV-2", timeline: 1, timelineAutre: 1));   // indécidable : refus
        Assert.NotNull(ReplicationOfficine.RefusDemarrage(false, true, "SRV-2"));
        Assert.Null(ReplicationOfficine.RefusDemarrage(false, false, "SRV-2"));                                     // l'autre est en veille
    }

    // ---- Réglages propres à chaque serveur ----

    [Fact]
    public void Le_reglage_du_serveur_l_emporte_sur_appsettings()
    {
        Assert.Equal(@"E:\CHIFA\CNAS_SIGNED", DossiersServeur.Choisir(@" E:\CHIFA\CNAS_SIGNED ", @"D:\CHIFA_OFFICINE\CNAS_SIGNED"));
        Assert.Equal(@"D:\CHIFA_OFFICINE\CNAS_SIGNED", DossiersServeur.Choisir(null, @"D:\CHIFA_OFFICINE\CNAS_SIGNED"));
        Assert.Equal(@"D:\CHIFA_OFFICINE\CNAS_SIGNED", DossiersServeur.Choisir("  ", @"D:\CHIFA_OFFICINE\CNAS_SIGNED"));
        Assert.Null(DossiersServeur.Choisir(null, null));
    }

    [Fact]
    public async Task Chaque_serveur_garde_ses_dossiers_et_un_chemin_relatif_est_refuse()
    {
        var depot = new DepotFactice();
        depot.Serveurs.Add(Serveur("SRV-1", RolesServeur.Principal));
        depot.Serveurs.Add(Serveur("SRV-2", RolesServeur.Second));
        var journal = new JournalEspion();
        var modifier = new ModifierServeurOfficine(depot, journal);

        await modifier.ExecuterAsync("srv-2", new ModificationServeur(@"E:\CHIFA", @"E:\MDN", true, true, false, true), 1);
        await modifier.ExecuterAsync("SRV-1", new ModificationServeur(@"D:\CHIFA_OFFICINE\CNAS_SIGNED", null, true, true, true, true), 1);

        Assert.Equal(@"E:\CHIFA", depot.Modifs["SRV-2"].DossierChifa);
        Assert.Equal(@"E:\MDN", depot.Modifs["SRV-2"].DossierMdn);
        Assert.Equal(@"D:\CHIFA_OFFICINE\CNAS_SIGNED", depot.Modifs["SRV-1"].DossierChifa);
        Assert.Equal(2, journal.Entrees.Count);
        await Assert.ThrowsAsync<RegleMetierException>(() => modifier.ExecuterAsync("SRV-2", new ModificationServeur(@"CHIFA\export", null, false, false, false, false), 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => modifier.ExecuterAsync("INCONNU", new ModificationServeur(null, null, false, false, false, false), 1));
    }

    // ---- Alerte de retard de la réplication ----

    [Fact]
    public async Task Retard_au_dela_du_seuil_alerte_le_titulaire_une_fois_par_heure()
    {
        var depot = new DepotFactice { Replication = Etat(connecte: true, secondes: 900, slot: true), Reglages = new ReglagesReplication(300) };
        var horloge = new Horloge();
        var alertes = new Alertes();
        var surveiller = new SurveillerReplication(depot, alertes, horloge);

        Assert.True(await surveiller.ExecuterAsync());
        Assert.Contains("en retard", Assert.Single(alertes.Messages));
        horloge.Avancer(TimeSpan.FromMinutes(30));
        Assert.False(await surveiller.ExecuterAsync());          // pas plus d'une alerte par heure
        horloge.Avancer(TimeSpan.FromMinutes(31));
        Assert.True(await surveiller.ExecuterAsync());
        Assert.Equal(2, alertes.Messages.Count);
    }

    [Fact]
    public async Task Replication_decrochee_alerte_et_retour_a_la_normale_remet_l_alerte_a_zero()
    {
        var depot = new DepotFactice { Replication = Etat(connecte: false, slot: true) };
        var alertes = new Alertes();
        var surveiller = new SurveillerReplication(depot, alertes, new Horloge());

        Assert.True(await surveiller.ExecuterAsync());
        Assert.Contains("décrochée", alertes.Messages[0]);
        depot.Replication = Etat(connecte: true, secondes: 2, octets: 1000, slot: true);
        Assert.False(await surveiller.ExecuterAsync());
        Assert.Null(depot.Reglages.DerniereAlerteUtc);
    }

    [Fact]
    public async Task Pas_d_alerte_sans_2e_serveur_ou_sous_le_seuil()
    {
        var alertes = new Alertes();
        Assert.False(await new SurveillerReplication(new DepotFactice { Replication = Etat(connecte: false) }, alertes, new Horloge()).ExecuterAsync());
        var depot = new DepotFactice { Replication = Etat(connecte: true, secondes: 10, octets: 1024, slot: true), Reglages = new ReglagesReplication(300, 1024 * 1024) };
        Assert.False(await new SurveillerReplication(depot, alertes, new Horloge()).ExecuterAsync());
        depot.Replication = Etat(connecte: true, secondes: 10, octets: 5 * 1024 * 1024, slot: true);
        Assert.True(await new SurveillerReplication(depot, alertes, new Horloge()).ExecuterAsync());   // seuil en octets
        Assert.Single(alertes.Messages);
    }

    [Fact]
    public async Task Seuils_hors_bornes_refuses()
    {
        var m = new ModifierSeuilsReplication(new DepotFactice(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => m.ExecuterAsync(5, 64, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => m.ExecuterAsync(300, 0, 1));
        await m.ExecuterAsync(120, 32, 1);
    }

    // ---- Doublures ----

    private static IEnumerable<string> Formes(byte[] cle) => [Convert.ToHexString(cle), Convert.ToBase64String(cle)];

    private static EtatReplication Etat(bool connecte, double? secondes = null, long? octets = null, bool slot = false) =>
        new(false, connecte, connecte ? "streaming" : null, null, octets, secondes, slot, slot && connecte, true);

    private static ServeurOfficine Serveur(string nom, string role) =>
        new(nom, role, null, null, false, false, false, false, null, null, null, null, null, null, null);

    private sealed class CleMemoire(byte[]? cle) : ICleIntegriteStockee
    {
        public byte[]? Cle { get; private set; } = cle;
        public string Emplacement => "memoire";
        public bool Presente => Cle is not null;
        public byte[] Lire() => (byte[])(Cle ?? throw new InvalidOperationException()).Clone();
        public void Ecrire(byte[] cle, bool remplacer)
        {
            if (Cle is not null && !remplacer) throw new InvalidOperationException();
            Cle = (byte[])cle.Clone();
        }
    }

    private sealed class JournalEspion : IJournal
    {
        public List<(string Action, IReadOnlyDictionary<string, object?> Details)> Entrees { get; } = [];
        public Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
            IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default)
        {
            Entrees.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc { get; private set; } = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
        public void Avancer(TimeSpan d) => MaintenantUtc += d;
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public List<string> Messages { get; } = [];
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) { Messages.Add(detail); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DepotFactice : ISecondServeurDepot
    {
        public List<ServeurOfficine> Serveurs { get; } = [];
        public Dictionary<string, ModificationServeur> Modifs { get; } = [];
        public EtatReplication Replication { get; set; } = Etat(connecte: false);
        public ReglagesReplication Reglages { get; set; } = new();

        public Task<IReadOnlyList<ServeurOfficine>> ServeursAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ServeurOfficine>>(Serveurs);
        public Task<EtatReplication> ReplicationAsync(string slot, CancellationToken ct = default) => Task.FromResult(Replication);
        public Task<ReglagesReplication> ReglagesAsync(CancellationToken ct = default) => Task.FromResult(Reglages);
        public Task EnregistrerSeuilsAsync(int seuilSecondes, long seuilOctets, CancellationToken ct = default)
        {
            Reglages = Reglages with { SeuilSecondes = seuilSecondes, SeuilOctets = seuilOctets };
            return Task.CompletedTask;
        }
        public Task NoterAlerteAsync(DateTime? alerteUtc, CancellationToken ct = default)
        {
            Reglages = Reglages with { DerniereAlerteUtc = alerteUtc };
            return Task.CompletedTask;
        }
        public Task<bool> ModifierServeurAsync(string nom, ModificationServeur m, long utilisateurId, CancellationToken ct = default)
        {
            if (Serveurs.All(s => s.Nom != nom)) return Task.FromResult(false);
            Modifs[nom] = m;
            return Task.FromResult(true);
        }
        public Task DeclarerPrincipalAsync(ServeurOfficine releve, CancellationToken ct = default) => Task.CompletedTask;
    }
}
