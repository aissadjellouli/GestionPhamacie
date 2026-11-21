using System.Reflection;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;
using Pharma.Application.Synchronisation;

namespace Pharma.Application.Tests;

/// <summary>Barre d'état : liaison avec le serveur central et le 2e serveur (élément seulement en anomalie ; rien sinon).</summary>
public class EtatLiaisonsPosteTests
{
    private static readonly DateTime Maintenant = new(2026, 11, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly CertificatSyncInfo CertValable = new("AB", Maintenant.AddDays(200), "CD");

    private static ParametresEnvoiSync Parametres(bool active = true, string? adresse = "https://central.exemple", bool inscrite = true, long lots = 3,
        bool lectureSeule = false) =>
        new(active, Guid.NewGuid(), 1, "v1", 10, 7, adresse, inscrite ? Maintenant.AddDays(-30) : null, Maintenant.AddMinutes(-5), Maintenant.AddMinutes(-5),
            null, null, "filtres", false, 12, lots, lectureSeule);

    private static EtatSupervisionSync Supervision(AttenteSync? attente = null, string? nature = null, DateTime? erreurDepuis = null,
        RetourArriereSync? retour = null, bool lectureSeule = false) =>
        new(true, lectureSeule, attente ?? new AttenteSync(null, 0), erreurDepuis, nature, "message technique", new Dictionary<string, DateTime>(),
            ReglagesSupervisionSync.Defaut, RetourArriere: retour);

    private static AnomalieLiaison? Central(ParametresEnvoiSync? p = null, EtatSupervisionSync? s = null, CertificatSyncInfo? cert = null, bool sansCert = false) =>
        RegleLiaisonsPoste.Central(p ?? Parametres(), s ?? Supervision(), sansCert ? null : cert ?? CertValable, Maintenant);

    [Fact]
    public void Central_rien_si_non_activee_sans_adresse_en_lecture_seule_ou_a_jour()
    {
        var enRetard = Supervision(new AttenteSync(Maintenant.AddDays(-3), 50));
        Assert.Null(Central(Parametres(active: false), enRetard));
        Assert.Null(Central(Parametres(adresse: null), enRetard));
        Assert.Null(Central(Parametres(adresse: "  "), enRetard));
        Assert.Null(Central(Parametres(lectureSeule: true), enRetard));
        Assert.Null(Central(s: Supervision(new AttenteSync(Maintenant.AddDays(-3), 50), lectureSeule: true)));
        Assert.Null(Central());                                                                  // à jour
        Assert.Null(Central(s: Supervision(new AttenteSync(Maintenant.AddMinutes(-59), 4))));     // sous le seuil du bandeau (60 min)
        Assert.Null(Central(s: Supervision(nature: NaturesErreurSync.Autre, erreurDepuis: Maintenant.AddMinutes(-30))));   // erreur pas encore persistante
    }

    [Fact]
    public void Central_libelles_et_niveaux_du_plus_grave_au_moins_grave()
    {
        var a = Central(p: Parametres(inscrite: false));
        Assert.Equal(("Central : activation à faire", NiveauxLiaison.Attention), (a!.Libelle, a.Niveau));
        Assert.Equal("Central : activation à faire", Central(sansCert: true)!.Libelle);

        var retour = new RetourArriereSync(Maintenant.AddHours(-1), 20, 15, 1, true, null);
        a = Central(s: Supervision(new AttenteSync(Maintenant.AddDays(-3), 50), retour: retour));
        Assert.Equal(("Central : envoi suspendu", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));

        a = Central(cert: CertValable with { EcheanceUtc = Maintenant.AddDays(-1) });
        Assert.Equal(("Central : certificat échu", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));

        a = Central(s: Supervision(nature: NaturesErreurSync.Quota));
        Assert.Equal(("Central : quota refusé", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));

        a = Central(s: Supervision(nature: NaturesErreurSync.Autre, erreurDepuis: Maintenant.AddMinutes(-60)));   // seuil d'erreur persistante atteint
        Assert.Equal(("Central : erreur d’envoi", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));

        a = Central(s: Supervision(new AttenteSync(Maintenant.AddHours(-24), 35)));   // seuil d'alerte de retard (24 h)
        Assert.Equal(("Central : en retard (24 h 00)", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));
        Assert.Contains("35 opération(s) en attente (3 lot(s) non accusé(s))", a.Detail);

        a = Central(cert: CertValable with { EcheanceUtc = Maintenant.AddDays(20) });
        Assert.Equal(("Central : activation à refaire", NiveauxLiaison.Attention), (a!.Libelle, a.Niveau));

        a = Central(s: Supervision(new AttenteSync(Maintenant.AddMinutes(-125), 35)));   // seuil du bandeau (60 min)
        Assert.Equal(("Central : en attente (2 h 05)", NiveauxLiaison.Attention), (a!.Libelle, a.Niveau));
    }

    [Fact]
    public void Central_le_detail_ne_reprend_ni_l_adresse_ni_le_message_d_erreur()
    {
        foreach (var a in new[]
        {
            Central(s: Supervision(nature: NaturesErreurSync.Autre, erreurDepuis: Maintenant.AddHours(-2))),
            Central(s: Supervision(new AttenteSync(Maintenant.AddHours(-30), 5))),
            Central(s: Supervision(nature: NaturesErreurSync.Quota))
        })
        {
            Assert.NotNull(a);
            Assert.DoesNotContain("central.exemple", a!.Detail);
            Assert.DoesNotContain("message technique", a.Detail);
        }
    }

    private static ServeurOfficine Serveur(string nom, string role) => new(nom, role, null, null, false, false, false, false, null, null, null, null, null, null, null);
    private static EtatReplication Replication(bool connecte = true, double? secondes = null, long? octets = null, bool slot = false, bool recovery = false) =>
        new(recovery, connecte, connecte ? "streaming" : null, null, octets, secondes, slot, slot && connecte, true);
    private static readonly IReadOnlyList<ServeurOfficine> AvecSecond = [Serveur("SRV-1", RolesServeur.Principal), Serveur("SRV-2", RolesServeur.Second)];

    [Fact]
    public void Second_rien_si_non_configure_vu_du_second_ou_a_jour()
    {
        var r = new ReglagesReplication();
        Assert.Null(RegleLiaisonsPoste.Second([Serveur("SRV-1", RolesServeur.Principal)], Replication(connecte: false), r));   // aucun 2e serveur ni slot
        Assert.Null(RegleLiaisonsPoste.Second(AvecSecond, Replication(connecte: false, recovery: true), r));                     // ce serveur est le 2e
        Assert.Null(RegleLiaisonsPoste.Second(AvecSecond, Replication(secondes: 12, octets: 1024), r));                          // à jour
    }

    [Fact]
    public void Second_decroche_ou_en_retard()
    {
        var r = new ReglagesReplication();
        var a = RegleLiaisonsPoste.Second(AvecSecond, Replication(connecte: false), r);
        Assert.Equal(("2e serveur : décroché", NiveauxLiaison.Anormal), (a!.Libelle, a.Niveau));
        a = RegleLiaisonsPoste.Second([Serveur("SRV-1", RolesServeur.Principal)], Replication(connecte: false, slot: true), r);   // slot seul
        Assert.Equal("2e serveur : décroché", a!.Libelle);
        a = RegleLiaisonsPoste.Second(AvecSecond, Replication(secondes: 900), r);
        Assert.Equal(("2e serveur : en retard (15 min)", NiveauxLiaison.Attention), (a!.Libelle, a.Niveau));
        a = RegleLiaisonsPoste.Second(AvecSecond, Replication(octets: 100L * 1024 * 1024), r);
        Assert.Equal(("2e serveur : en retard (100 Mo)", NiveauxLiaison.Attention), (a!.Libelle, a.Niveau));
    }

    [Fact]
    public void Libelles_courts_pour_tenir_sur_une_ligne()
    {
        var libelles = new[]
        {
            Central(p: Parametres(inscrite: false)), Central(cert: CertValable with { EcheanceUtc = Maintenant.AddDays(-1) }),
            Central(cert: CertValable with { EcheanceUtc = Maintenant.AddDays(20) }), Central(s: Supervision(new AttenteSync(Maintenant.AddDays(-300), 5))),
            Central(s: Supervision(new AttenteSync(Maintenant.AddMinutes(-23 * 60 - 59), 5))),
            RegleLiaisonsPoste.Second(AvecSecond, Replication(secondes: 86_000), new ReglagesReplication()),
            RegleLiaisonsPoste.Second(AvecSecond, Replication(octets: 99_999L * 1024 * 1024), new ReglagesReplication())
        }.Select(a => a!.Libelle).ToList();
        Assert.All(libelles, l => Assert.True(l.Length <= 34, $"libellé trop long : {l}"));
    }

    // ---- Lecture (mémoire de 30 secondes, une liaison illisible n'empêche pas l'autre) ----

    private sealed class Horloge(DateTime debut) : IHorloge
    {
        public DateTime MaintenantUtc { get; set; } = debut;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    /// <summary>Doublure d'interface : les méthodes nommées répondent, les autres lèvent une exception.</summary>
    public class Doublure : DispatchProxy
    {
        public Dictionary<string, Func<object?>> Reponses { get; } = [];
        public int Appels;
        protected override object? Invoke(MethodInfo? methode, object?[]? args)
        {
            Appels++;
            return Reponses.TryGetValue(methode!.Name, out var f) ? f() : throw new InvalidOperationException($"{methode.Name} non prévu");
        }
    }

    private static (T Objet, Doublure Doublure) Creer<T>() where T : class
    {
        var o = DispatchProxy.Create<T, Doublure>();
        return (o, (Doublure)(object)o);
    }

    private sealed class Certificat(CertificatSyncInfo? c) : ICertificatSync
    {
        public CertificatSyncInfo? Lire() => c;
        public string NouvelleDemande() => throw new NotSupportedException();
        public void Installer(string certificatPem, string autoritePem, string? epinglage) => throw new NotSupportedException();
        public System.Security.Cryptography.X509Certificates.X509Certificate2? CertificatClient() => null;
        public System.Security.Cryptography.X509Certificates.X509Certificate2? Autorite() => null;
    }

    [Fact]
    public async Task Lecture_gardee_30_secondes_et_liaison_illisible_sans_rien()
    {
        var horloge = new Horloge(Maintenant);
        var (envoi, de) = Creer<IEnvoiSyncDepot>();
        de.Reponses["ParametresAsync"] = () => Task.FromResult(Parametres());
        var (supervision, ds) = Creer<ISupervisionSyncDepot>();
        ds.Reponses["LireAsync"] = () => Task.FromResult(Supervision(new AttenteSync(Maintenant.AddHours(-2), 35)));
        var (second, _) = Creer<ISecondServeurDepot>();   // aucune réponse prévue : 2e serveur illisible
        var memoire = new MemoireLiaisonsPoste(horloge);
        var consulter = new ConsulterLiaisonsPoste(envoi, supervision, new MemoireSupervisionSync(horloge), new Certificat(CertValable), second, memoire, horloge);

        var e = await consulter.ExecuterAsync();
        Assert.True(e.CentralActive);
        Assert.Equal("Central : en attente (2 h 00)", e.Central!.Libelle);
        Assert.Equal(3, e.LotsEnAttente);
        Assert.False(e.SecondConfigure);
        Assert.Null(e.Second);

        var appels = de.Appels;
        horloge.MaintenantUtc = Maintenant.AddSeconds(29);
        Assert.Same(e, await consulter.ExecuterAsync());
        Assert.Equal(appels, de.Appels);
        horloge.MaintenantUtc = Maintenant.AddSeconds(31);
        await consulter.ExecuterAsync();
        Assert.Equal(appels + 1, de.Appels);
    }

    [Fact]
    public async Task Lecture_non_activee_rien_au_central_et_second_decroche()
    {
        var horloge = new Horloge(Maintenant);
        var (envoi, de) = Creer<IEnvoiSyncDepot>();
        de.Reponses["ParametresAsync"] = () => Task.FromResult(Parametres(active: false));
        var (supervision, ds) = Creer<ISupervisionSyncDepot>();
        ds.Reponses["LireAsync"] = () => Task.FromResult(Supervision(new AttenteSync(Maintenant.AddDays(-2), 35)));
        var (second, d2) = Creer<ISecondServeurDepot>();
        d2.Reponses["ServeursAsync"] = () => Task.FromResult(AvecSecond);
        d2.Reponses["ReplicationAsync"] = () => Task.FromResult(Replication(connecte: false));
        d2.Reponses["ReglagesAsync"] = () => Task.FromResult(new ReglagesReplication());
        var e = await new ConsulterLiaisonsPoste(envoi, supervision, new MemoireSupervisionSync(horloge), new Certificat(CertValable), second,
            new MemoireLiaisonsPoste(horloge), horloge).ExecuterAsync();
        Assert.False(e.CentralActive);
        Assert.Null(e.Central);
        Assert.Equal(0, e.LotsEnAttente);
        Assert.Null(e.DernierEnvoiLe);
        Assert.True(e.SecondConfigure);
        Assert.Equal("2e serveur : décroché", e.Second!.Libelle);
    }
}
