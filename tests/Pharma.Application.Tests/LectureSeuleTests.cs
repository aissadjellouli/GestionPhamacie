using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>Bascule du 2e serveur, lot 3 : gardien (verrou + écart gardé en base) et démarrage en lecture seule (Q8, Q10).</summary>
public class LectureSeuleTests
{
    private const long Lignee = 7_000_000_000_000_000_001;
    private static ReleveServeur Ecriture(long timeline, long? lignee = Lignee) => new(false, timeline, lignee);
    private static EcartDetecte Ecart(bool indecis = false) => new(new DateTime(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc), indecis ? null : "SRV-2", 1, indecis ? 1 : 2, indecis);

    [Fact]
    public async Task Passage_a_ecarte_verrouille_la_base_et_ecrit_l_ecart_puis_la_levee_rend_l_ecriture()
    {
        var f = new Fabrique();
        f.Gardien.Demarrer();

        f.Suivi.Restaurer(Ecart());
        await f.Gardien.Derniere;

        Assert.Equal([true], f.Verrou.Appels);
        var ecrit = Assert.Single(f.Depot.Ecrits);
        Assert.Contains("SRV-2", ecrit.Motif);
        f.Suivi.Lever();
        Assert.Equal([true, false], f.Verrou.Appels);
    }

    [Fact]
    public async Task Etat_indecis_verrouille_sans_ecrire_en_base()
    {
        var f = new Fabrique();
        f.Gardien.Demarrer();
        f.Suivi.Restaurer(Ecart(indecis: true));
        await f.Gardien.Derniere;
        Assert.Equal([true], f.Verrou.Appels);
        Assert.Empty(f.Depot.Ecrits);
        await f.Gardien.SynchroniserAsync();   // jamais de levée ni d'écriture pour un état indécis
        Assert.NotNull(f.Suivi.Ecart);
        Assert.Empty(f.Depot.Ecrits);
    }

    [Fact]
    public async Task Charger_restaure_l_ecart_garde_meme_si_l_autre_serveur_ne_repond_plus()
    {
        var f = new Fabrique();
        f.Depot.Actif = Ecart();
        f.Gardien.Demarrer();

        Assert.True(await f.Gardien.ChargerAsync());

        Assert.Equal(Ecart(), f.Suivi.Ecart);
        Assert.Equal([true], f.Verrou.Appels);
    }

    [Fact]
    public async Task Charger_base_illisible_ne_bloque_pas_le_demarrage_et_trace_l_erreur()
    {
        var f = new Fabrique();
        f.Depot.Panne = true;
        Assert.False(await f.Gardien.ChargerAsync());
        Assert.Null(f.Suivi.Ecart);
        Assert.Single(f.Trace.Messages);
    }

    [Fact]
    public async Task Synchroniser_reecrit_un_ecart_non_ecrit_puis_constate_la_levee_faite_en_base()
    {
        var f = new Fabrique();
        f.Gardien.Demarrer();
        f.Depot.Panne = true;
        f.Suivi.Restaurer(Ecart());
        await f.Gardien.Derniere;
        Assert.Empty(f.Depot.Ecrits);   // écriture échouée, tracée

        f.Depot.Panne = false;
        await f.Gardien.SynchroniserAsync();
        Assert.Single(f.Depot.Ecrits);
        Assert.NotNull(f.Suivi.Ecart);

        f.Depot.Actif = null;   // la commande console --lever-ecart a levé la ligne
        await f.Gardien.SynchroniserAsync();
        Assert.Null(f.Suivi.Ecart);
        Assert.Equal([true, false], f.Verrou.Appels);
    }

    // ---- Démarrage (Q8, Q10) ----

    [Fact]
    public async Task Demarrage_face_a_un_principal_plus_recent_de_meme_lignee_ecarte_ce_serveur()
    {
        var releves = new Releves { Local = Ecriture(1), Autre = Ecriture(2) };
        var ecart = await DemarrageEcarte.EvaluerAsync(releves, new Horloge(), TimeSpan.Zero);
        Assert.NotNull(ecart);
        Assert.False(ecart.Indecis);
        Assert.Equal(("AUTRE", 1L, 2L), (ecart.ServeurRetenu, ecart.TimelineLocale, ecart.TimelineRetenue));
    }

    [Fact]
    public async Task Demarrage_serveur_retenu_ne_change_rien()
    {
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(3), Autre = Ecriture(2) }, new Horloge(), TimeSpan.Zero));
    }

    [Fact]
    public async Task Demarrage_sans_decision_possible_ne_change_rien_lignee_illisible_autre_injoignable_ou_en_veille()
    {
        var h = new Horloge();
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(1, null), Autre = Ecriture(2, null) }, h, TimeSpan.Zero));
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(1), Autre = null }, h, TimeSpan.Zero));
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(1), Autre = new ReleveServeur(true, null, null) }, h, TimeSpan.Zero));
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(1), Autre = Ecriture(2), Regle = false }, h, TimeSpan.Zero));
        Assert.Null(await DemarrageEcarte.EvaluerAsync(new Releves { Local = Ecriture(1, 11), Autre = Ecriture(2, 22) }, h, TimeSpan.Zero));
    }

    [Fact]
    public async Task Demarrage_meme_timeline_alerte_le_titulaire_avant_le_verrou_et_pose_un_etat_indecis()
    {
        var f = new Fabrique();
        f.Gardien.Demarrer();
        var releves = new Releves { Local = Ecriture(2), Autre = Ecriture(2) };

        var ecart = await DemarrageEcarte.AppliquerAsync(releves, f.Suivi, f.Alertes, new Horloge(), TimeSpan.Zero);

        Assert.True(ecart!.Indecis);
        Assert.Contains("même timeline", Assert.Single(f.Alertes.Messages));
        Assert.True(f.Suivi.Ecart!.Indecis);
        Assert.Equal([true], f.Verrou.Appels);
    }

    [Fact]
    public async Task Sans_2e_serveur_une_ligne_active_est_ignoree_aucun_verrou_aucune_lecture_seule()
    {
        var f = new Fabrique(deuxServeurs: false);
        f.Depot.Actif = Ecart();
        f.Gardien.Demarrer();

        Assert.False(await f.Gardien.ChargerAsync());
        await f.Gardien.SynchroniserAsync();

        Assert.Null(f.Suivi.Ecart);          // le barrage (503) lit cet état : inactif
        Assert.Empty(f.Verrou.Appels);       // connexions normales
        Assert.Equal(Ecart(), f.Gardien.EcartIgnore);   // avertissement au journal par Program
        Assert.Empty(f.Depot.Ecrits);
    }

    [Fact]
    public async Task Avec_2e_serveur_une_ligne_active_verrouille_comme_avant()
    {
        var f = new Fabrique(deuxServeurs: true);
        f.Depot.Actif = Ecart();
        f.Gardien.Demarrer();

        Assert.True(await f.Gardien.ChargerAsync());

        Assert.NotNull(f.Suivi.Ecart);
        Assert.Equal([true], f.Verrou.Appels);
        Assert.Null(f.Gardien.EcartIgnore);
    }

    [Fact]
    public async Task Sans_2e_serveur_le_demarrage_ne_decide_ni_Q8_ni_Q10_et_avec_il_decide()
    {
        var f = new Fabrique(deuxServeurs: false);
        var sans = new Releves { Regle = false, Local = Ecriture(1), Autre = Ecriture(2) };
        Assert.Null(await DemarrageEcarte.AppliquerAsync(sans, f.Suivi, f.Alertes, new Horloge(), TimeSpan.Zero));
        Assert.Null(f.Suivi.Ecart);
        Assert.Equal(0, sans.Appels);

        var avec = new Releves { Local = Ecriture(1), Autre = Ecriture(2) };
        Assert.NotNull(await DemarrageEcarte.AppliquerAsync(avec, f.Suivi, f.Alertes, new Horloge(), TimeSpan.Zero));
    }

    [Fact]
    public async Task Demarrage_deja_ecarte_ne_relit_rien()
    {
        var f = new Fabrique();
        f.Suivi.Restaurer(Ecart());
        var releves = new Releves { Local = Ecriture(1), Autre = Ecriture(2) };
        Assert.Null(await DemarrageEcarte.AppliquerAsync(releves, f.Suivi, f.Alertes, new Horloge(), TimeSpan.Zero));
        Assert.Equal(0, releves.Appels);
    }

    private sealed class Fabrique
    {
        public SuiviServeurEcarte Suivi { get; } = new();
        public Verrou Verrou { get; } = new();
        public Depot Depot { get; } = new();
        public Trace Trace { get; } = new();
        public Alertes Alertes { get; } = new();
        public GardienServeurEcarte Gardien { get; }
        public Releves Releves { get; } = new();
        public Fabrique(bool deuxServeurs = true) { Releves.Regle = deuxServeurs; Gardien = new GardienServeurEcarte(Suivi, Verrou, Depot, Releves, Trace); }
    }

    private sealed class Verrou : IVerrouBase
    {
        public List<bool> Appels { get; } = [];
        public void Appliquer(bool lectureSeule) => Appels.Add(lectureSeule);
    }

    private sealed class Depot : IEcartsDepot
    {
        public EcartDetecte? Actif { get; set; }
        public bool Panne { get; set; }
        public List<(EcartDetecte Ecart, string Motif)> Ecrits { get; } = [];
        public Task<EcartDetecte?> LireActifAsync(CancellationToken ct = default) => Panne ? throw new InvalidOperationException("base illisible") : Task.FromResult(Actif);
        public Task<bool> EnregistrerAsync(EcartDetecte ecart, string motif, CancellationToken ct = default)
        {
            if (Panne) throw new InvalidOperationException("base illisible");
            Ecrits.Add((ecart, motif));
            Actif = ecart;
            return Task.FromResult(true);
        }
        public Task<bool> LeverAsync(string par, string motif, CancellationToken ct = default) { var a = Actif is not null; Actif = null; return Task.FromResult(a); }
    }

    private sealed class Trace : ITraceErreurs
    {
        public List<string> Messages { get; } = [];
        public void Erreur(string message, Exception exception) => Messages.Add(message);
    }

    private sealed class Releves : IReleveServeurs
    {
        public bool Regle { get; set; } = true;
        public ReleveServeur? Local { get; set; }
        public ReleveServeur? Autre { get; set; }
        public int Appels { get; private set; }
        public bool AutreServeurRegle => Regle;
        public string? AutreServeur => "AUTRE";
        public Task<ReleveServeur?> LocalAsync(CancellationToken ct = default) { Appels++; return Task.FromResult(Local); }
        public Task<ReleveServeur?> AutreAsync(CancellationToken ct = default) { Appels++; return Task.FromResult(Autre); }
    }

    private sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc { get; } = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public List<string> Messages { get; } = [];
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) { Messages.Add(detail); return Task.CompletedTask; }
        public Task SecondServeurAsync(string objet, string detail, string sms, CancellationToken ct = default) { Messages.Add(detail); return Task.CompletedTask; }   // adapté : mécanisme unique du lot 5
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
