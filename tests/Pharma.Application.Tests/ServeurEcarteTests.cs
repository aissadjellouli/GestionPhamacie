using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.SecondServeur;

namespace Pharma.Application.Tests;

/// <summary>Bascule du 2e serveur, lot 1 : décision pure « qui est écarté » et boucle de surveillance (horloge simulée).</summary>
public class ServeurEcarteTests
{
    private const long Lignee = 7_000_000_000_000_000_001;

    private static ReleveServeur Ecriture(long timeline, long? lignee = Lignee) => new(false, timeline, lignee);
    private static readonly ReleveServeur Copie = new(true, null, null);

    // ---- Décision pure ----

    [Fact]
    public void Timeline_de_l_autre_plus_grande_ce_serveur_est_ecarte_apres_deux_releves()
    {
        var premier = DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(2));
        Assert.Equal(VerdictEcart.Ecarte, premier.Verdict);
        Assert.False(premier.Confirme);   // un seul relevé : jamais de décision

        var second = DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(2), premier);
        Assert.Equal(VerdictEcart.Ecarte, second.Verdict);
        Assert.True(second.Confirme);
        Assert.Equal((1L, 2L), (second.TimelineLocale, second.TimelineAutre));
    }

    [Fact]
    public void Timeline_de_l_autre_plus_petite_ce_serveur_est_retenu()
    {
        var premier = DecisionServeurEcarte.Evaluer(Ecriture(3), Ecriture(2));
        var second = DecisionServeurEcarte.Evaluer(Ecriture(3), Ecriture(2), premier);
        Assert.Equal(VerdictEcart.Retenu, second.Verdict);
        Assert.True(second.Confirme);
    }

    [Fact]
    public void Timelines_egales_deux_bases_en_ecriture_verdict_dedie_sans_ecarter()
    {
        var premier = DecisionServeurEcarte.Evaluer(Ecriture(2), Ecriture(2));
        var second = DecisionServeurEcarte.Evaluer(Ecriture(2), Ecriture(2), premier);
        Assert.Equal(VerdictEcart.TimelinesEgales, second.Verdict);
    }

    [Fact]
    public void Lignee_differente_jamais_ecarte()
    {
        var d = DecisionServeurEcarte.Evaluer(Ecriture(1, lignee: 11), Ecriture(9, lignee: 22));
        Assert.Equal(VerdictEcart.LigneeDifferente, d.Verdict);
        Assert.NotEqual(VerdictEcart.Ecarte, DecisionServeurEcarte.Evaluer(Ecriture(1, 11), Ecriture(9, 22), d).Verdict);
    }

    [Fact]
    public void Autre_serveur_injoignable_ou_releve_local_illisible_est_inconnu()
    {
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(Ecriture(1), null).Verdict);
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(null, Ecriture(2)).Verdict);
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(null, null).Verdict);
        Assert.False(DecisionServeurEcarte.Evaluer(Ecriture(1), null, new DecisionEcart(VerdictEcart.Ecarte, false, 1, 2)).Confirme);
    }

    [Fact]
    public void Lignee_ou_timeline_illisible_est_inconnu()
    {
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(Ecriture(1, null), Ecriture(2)).Verdict);
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(2, null)).Verdict);
        Assert.Equal(VerdictEcart.Inconnu, DecisionServeurEcarte.Evaluer(new ReleveServeur(false, null, Lignee), Ecriture(2)).Verdict);
    }

    [Fact]
    public void Une_base_en_lecture_seule_est_normal()
    {
        Assert.Equal(VerdictEcart.Normal, DecisionServeurEcarte.Evaluer(Ecriture(2), Copie).Verdict);
        Assert.Equal(VerdictEcart.Normal, DecisionServeurEcarte.Evaluer(Copie, Ecriture(2)).Verdict);
        Assert.Equal(VerdictEcart.Normal, DecisionServeurEcarte.Evaluer(Copie, Copie).Verdict);
    }

    [Fact]
    public void Releves_non_concordants_ne_confirment_pas()
    {
        var avant = DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(2));
        Assert.False(DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(3), avant).Confirme);   // l'autre a encore changé de timeline
        Assert.False(DecisionServeurEcarte.Evaluer(Ecriture(2), Ecriture(1), avant).Confirme);   // verdict inversé
        Assert.False(DecisionServeurEcarte.Evaluer(Ecriture(1), Ecriture(2), new DecisionEcart(VerdictEcart.Inconnu, false)).Confirme);
    }

    // ---- Boucle (horloge simulée) ----

    [Fact]
    public async Task Boucle_ecarte_au_deuxieme_passage_l_etat_est_colle_et_aucune_alerte_cote_ecarte()
    {
        var f = new Fabrique { Local = Ecriture(1), Autre = Ecriture(2) };
        var changements = new List<EcartDetecte?>();
        f.Suivi.Change += changements.Add;

        await f.PasserAsync();
        Assert.Null(f.Suivi.Ecart);   // un relevé ne suffit pas
        f.Horloge.Avancer(SurveillerServeurEcarte.Intervalle);
        await f.PasserAsync();

        var ecart = f.Suivi.Ecart;
        Assert.NotNull(ecart);
        Assert.Equal((1L, 2L), (ecart.TimelineLocale, ecart.TimelineRetenue));
        Assert.Equal("AUTRE", ecart.ServeurRetenu);
        Assert.Equal(f.Horloge.MaintenantUtc, ecart.DepuisUtc);
        Assert.Single(changements);
        Assert.Empty(f.Alertes.Messages);   // alerte du côté écarté : lot 5

        f.Autre = null;   // l'autre devient injoignable : l'état reste
        f.Horloge.Avancer(SurveillerServeurEcarte.Intervalle);
        Assert.Equal(VerdictEcart.Ecarte, (await f.PasserAsync())!.Verdict);
        Assert.NotNull(f.Suivi.Ecart);
        Assert.Equal(4, f.Releves.Appels);   // 2 passages x (local + autre) ; plus aucun relevé une fois écarté
    }

    [Fact]
    public async Task Boucle_un_releve_inconnu_entre_deux_releves_casse_la_confirmation()
    {
        var f = new Fabrique { Local = Ecriture(1), Autre = Ecriture(2) };
        await f.PasserAsync();
        f.Autre = null;   // coupure du réseau
        await f.PasserAsync();
        f.Autre = Ecriture(2);
        await f.PasserAsync();
        Assert.Null(f.Suivi.Ecart);   // 3 passages, jamais 2 relevés concordants consécutifs
        await f.PasserAsync();
        Assert.NotNull(f.Suivi.Ecart);
    }

    [Fact]
    public async Task Boucle_cote_retenu_alerte_le_titulaire_une_fois_par_heure_sans_rien_changer()
    {
        var f = new Fabrique { Local = Ecriture(3), Autre = Ecriture(2) };

        await f.PasserAsync();
        Assert.Empty(f.Alertes.Messages);
        await f.PasserAsync();
        Assert.Contains("ancien serveur principal", Assert.Single(f.Alertes.Messages));
        f.Horloge.Avancer(TimeSpan.FromMinutes(30));
        await f.PasserAsync();
        Assert.Single(f.Alertes.Messages);
        f.Horloge.Avancer(TimeSpan.FromMinutes(31));
        await f.PasserAsync();
        Assert.Equal(2, f.Alertes.Messages.Count);
        Assert.Null(f.Suivi.Ecart);

        f.Autre = Copie;   // l'ancien est remis en 2e serveur : rétabli, l'alerte repart sans délai à la prochaine récidive
        await f.PasserAsync();
        f.Autre = Ecriture(2);
        await f.PasserAsync();
        await f.PasserAsync();
        Assert.Equal(3, f.Alertes.Messages.Count);
    }

    [Fact]
    public async Task Boucle_lignee_differente_alerte_sans_ecarter()
    {
        var f = new Fabrique { Local = Ecriture(1, 11), Autre = Ecriture(5, 22) };
        await f.PasserAsync();
        await f.PasserAsync();
        Assert.Contains("identifiant système différent", Assert.Single(f.Alertes.Messages));
        Assert.Null(f.Suivi.Ecart);
    }

    [Fact]
    public async Task Boucle_timelines_egales_ni_ecart_ni_alerte()
    {
        var f = new Fabrique { Local = Ecriture(2), Autre = Ecriture(2) };
        await f.PasserAsync();
        Assert.Equal(VerdictEcart.TimelinesEgales, (await f.PasserAsync())!.Verdict);
        Assert.Null(f.Suivi.Ecart);
        Assert.Empty(f.Alertes.Messages);
    }

    [Fact]
    public async Task Boucle_sans_autre_serveur_regle_ou_en_veille_ne_lit_pas_l_autre()
    {
        var f = new Fabrique { Local = Ecriture(1), Autre = Ecriture(2), Regle = false };
        Assert.Null(await f.PasserAsync());
        Assert.Equal(0, f.Releves.Appels);

        var veille = new Fabrique { Local = Copie, Autre = Ecriture(2) };
        Assert.Equal(VerdictEcart.Normal, (await veille.PasserAsync())!.Verdict);
        Assert.Equal(1, veille.Releves.Appels);   // seulement le relevé local
    }

    [Fact]
    public void Lever_efface_l_etat_et_restaurer_le_reprend_une_seule_fois()
    {
        var suivi = new SuiviServeurEcarte();
        var notifies = new List<EcartDetecte?>();
        suivi.Change += notifies.Add;
        var ecart = new EcartDetecte(DateTime.UtcNow, "AUTRE", 1, 2);

        suivi.Restaurer(ecart);
        suivi.Restaurer(ecart with { TimelineRetenue = 9 });
        Assert.Equal(2, suivi.Ecart!.TimelineRetenue);
        suivi.Lever();
        Assert.Null(suivi.Ecart);
        Assert.Equal([ecart, null], notifies);
    }

    private sealed class Fabrique
    {
        public ReleveServeur? Local { get => Releves.Local; set => Releves.Local = value; }
        public ReleveServeur? Autre { get => Releves.Autre; set => Releves.Autre = value; }
        public bool Regle { get => Releves.Regle; set => Releves.Regle = value; }
        public Releves Releves { get; } = new();
        public SuiviServeurEcarte Suivi { get; } = new();
        public Alertes Alertes { get; } = new();
        public Horloge Horloge { get; } = new();
        public Task<DecisionEcart?> PasserAsync() => new SurveillerServeurEcarte(Releves, Suivi, Alertes, Horloge).ExecuterAsync();
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
        public DateTime MaintenantUtc { get; private set; } = new(2026, 11, 23, 9, 0, 0, DateTimeKind.Utc);
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
}
