using System.Reflection;
using Pharma.Application.Achats;
using Pharma.Application.Administration;
using Pharma.Application.Caisse;
using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Application.Integrite;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>
/// Barre d'état : compteurs des pastilles lus en une fois pour tout employé connecté, chacun rendu seulement à qui a le droit de l'écran,
/// gardés 30 secondes ; une erreur de lecture laisse le compteur à 0 sans rien bloquer.
/// </summary>
public class CompteursBarreTests
{
    /// <summary>Faux dépôt : chaque méthode répond par la valeur réglée (sinon NotSupportedException) ; les appels sont comptés.</summary>
    public class Faux : DispatchProxy
    {
        public Dictionary<string, Func<object?>> Reponses { get; } = [];
        public List<string> Appels { get; } = [];
        protected override object? Invoke(MethodInfo? m, object?[]? args)
        {
            Appels.Add(m!.Name);
            return Reponses.TryGetValue(m.Name, out var r) ? r() : throw new NotSupportedException(m.Name);
        }
    }

    private sealed class Horloge(DateTime utc) : IHorloge
    {
        public DateTime MaintenantUtc { get; set; } = utc;
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
    }

    private sealed class Trace : ITraceErreurs
    {
        public readonly List<string> Messages = [];
        public void Erreur(string message, Exception exception) => Messages.Add(message);
    }

    private static (T Depot, Faux Faux) Creer<T>(Action<Faux> regler) where T : class
    {
        var d = DispatchProxy.Create<T, Faux>();
        var f = (Faux)(object)d;
        regler(f);
        return (d, f);
    }

    private static IReadOnlyList<T> Liste<T>(int n) => Enumerable.Repeat<T>(default!, n).ToList();

    private sealed record Monde(ConsulterCompteursBarre Cas, Horloge Horloge, Trace Trace, Faux Ventes, Faux Demandes, Faux Volets, Faux Bordereaux,
        Faux Traites, Faux Integrite);

    private static Monde Creer(DroitsFactices droits, MemoireCompteursBarre? memoire = null, Horloge? horloge = null, bool journauxRompus = true,
        bool bordereauxEnErreur = false)
    {
        var h = horloge ?? new Horloge(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc));
        var (ventes, fv) = Creer<IVentesEnAttente>(f => f.Reponses[nameof(IVentesEnAttente.ListerAsync)] = () => Task.FromResult(Liste<VenteEnAttente>(3)));
        var (demandes, fd) = Creer<IDemandesPatients>(f =>
        {
            f.Reponses[nameof(IDemandesPatients.DisponiblesAsync)] = () => Task.FromResult(Liste<DemandePatient>(1));
            f.Reponses[nameof(IDemandesPatients.ProduitsDusDisponiblesAsync)] = () => Task.FromResult(Liste<ProduitDuEnAttente>(1));
        });
        var (volets, fo) = Creer<IRapprochementsVolet>(f => f.Reponses[nameof(IRapprochementsVolet.EnAttenteAsync)] =
            () => Task.FromResult<(IReadOnlyList<VenteAttenteVolet>, int)>(([], 5)));
        var (bordereaux, fb) = Creer<IBordereaux>(f => f.Reponses[nameof(IBordereaux.CompteursAsync)] = bordereauxEnErreur
            ? () => throw new InvalidOperationException("base indisponible")
            : () => Task.FromResult(new CompteursBordereaux(12, 2, 1000m, 1, 500m, 0)));
        var (traites, ft) = Creer<ITraites>(f => f.Reponses[nameof(ITraites.ListerAsync)] =
            () => Task.FromResult(new Page<TraiteAPayer>([], 2, 1, 5)));
        var (integrite, fi) = Creer<IIntegriteDepot>(f => f.Reponses[nameof(IIntegriteDepot.DernieresVerificationsAsync)] =
            () => Task.FromResult<IReadOnlyList<DerniereVerification>>([
                new("ventes", null, h.MaintenantUtc, 10, 1, VerifierIntegrite.Ok, null, null, null, null, null),
                new("caisse", null, h.MaintenantUtc, 10, 1, journauxRompus ? VerifierIntegrite.Rupture : VerifierIntegrite.Ok, 4, null, null, null, null)]));
        var t = new Trace();
        var cas = new ConsulterCompteursBarre(ventes, demandes, volets, bordereaux, traites, integrite, droits, memoire ?? new MemoireCompteursBarre(h), h, t);
        return new Monde(cas, h, t, fv, fd, fo, fb, ft, fi);
    }

    [Fact]
    public async Task Vendeur_seulement_les_compteurs_du_comptoir()
    {
        var m = Creer(new DroitsFactices(Permissions.CaisseVendre));
        var c = await m.Cas.ExecuterAsync(7, "Caisse 1");
        Assert.Equal(new CompteursBarre(3, 2, 5, 0, 0, false), c);
    }

    [Fact]
    public async Task Droits_Chifa_et_finances_sans_droit_de_vendre_ni_titulaire()
    {
        var m = Creer(new DroitsFactices(Permissions.ChifaBordereaux, Permissions.AchatsRegler));
        var c = await m.Cas.ExecuterAsync(7, "Caisse 1");
        Assert.Equal(new CompteursBarre(0, 0, 0, 12, 2, false), c);
        Assert.Empty(m.Ventes.Appels);   // ventes en attente du poste : lues seulement avec le droit de vendre
    }

    [Fact]
    public async Task Titulaire_tous_les_compteurs_et_les_journaux_en_anomalie()
    {
        var titulaire = new DroitsFactices(Permissions.CaisseVendre, Permissions.ChifaBordereaux, Permissions.AchatsRegler) { Titulaire = true };
        Assert.Equal(new CompteursBarre(3, 2, 5, 12, 2, true), await Creer(titulaire).Cas.ExecuterAsync(1, "Caisse 1"));
        Assert.False((await Creer(titulaire, journauxRompus: false).Cas.ExecuterAsync(1, "Caisse 1")).JournauxEnAnomalie);
        // Journaux : jamais pour un employé qui n'est pas titulaire, même avec tous les droits du comptoir
        Assert.False((await Creer(new DroitsFactices(Permissions.CaisseVendre, Permissions.AchatsRegler)).Cas.ExecuterAsync(7, "Caisse 1")).JournauxEnAnomalie);
    }

    [Fact]
    public async Task Employe_sans_aucun_de_ces_droits_rien_et_aucune_lecture()
    {
        var m = Creer(new DroitsFactices("stock.consulter"));
        Assert.Equal(CompteursBarre.Rien, await m.Cas.ExecuterAsync(7, "Caisse 1"));
        Assert.Empty(m.Ventes.Appels.Concat(m.Demandes.Appels).Concat(m.Volets.Appels).Concat(m.Bordereaux.Appels).Concat(m.Traites.Appels)
            .Concat(m.Integrite.Appels));
    }

    [Fact]
    public async Task Poste_inconnu_aucune_vente_en_attente()
    {
        var m = Creer(new DroitsFactices(Permissions.CaisseVendre));
        Assert.Equal(0, (await m.Cas.ExecuterAsync(7, null)).VentesEnAttente);
        Assert.Equal(0, (await m.Cas.ExecuterAsync(7, "  ")).VentesEnAttente);
        Assert.Empty(m.Ventes.Appels);
    }

    [Fact]
    public async Task Gardes_30_secondes_puis_relus()
    {
        var horloge = new Horloge(new DateTime(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc));
        var memoire = new MemoireCompteursBarre(horloge);
        var vendeur = new DroitsFactices(Permissions.CaisseVendre, Permissions.ChifaBordereaux);
        var m = Creer(vendeur, memoire, horloge);
        await m.Cas.ExecuterAsync(7, "Caisse 1");
        horloge.MaintenantUtc = horloge.MaintenantUtc.AddSeconds(29);
        // Autre requête (autre employé, même poste) dans les 30 s : rien n'est relu
        await Creer(vendeur, memoire, horloge).Cas.ExecuterAsync(8, "Caisse 1");
        Assert.Single(m.Bordereaux.Appels);
        Assert.Single(m.Ventes.Appels);
        horloge.MaintenantUtc = horloge.MaintenantUtc.AddSeconds(1);
        await m.Cas.ExecuterAsync(7, "Caisse 1");
        Assert.Equal(2, m.Bordereaux.Appels.Count);
        Assert.Equal(2, m.Ventes.Appels.Count);
    }

    [Fact]
    public async Task Un_compteur_illisible_reste_a_zero_sans_bloquer_les_autres()
    {
        var m = Creer(new DroitsFactices(Permissions.CaisseVendre, Permissions.ChifaBordereaux), bordereauxEnErreur: true);
        var c = await m.Cas.ExecuterAsync(7, "Caisse 1");
        Assert.Equal(new CompteursBarre(3, 2, 5, 0, 0, false), c);
        Assert.Contains("Compteur de la barre d'état illisible : ventes Chifa à déposer", m.Trace.Messages);
    }

    [Fact]
    public void Filtre_par_droits_sans_base()
    {
        var bruts = new CompteursBarre(3, 2, 5, 12, 2, true);
        Assert.Equal(CompteursBarre.Rien, RegleCompteursBarre.Filtrer(bruts, [], titulaire: false));
        Assert.Equal(new CompteursBarre(0, 0, 0, 0, 2, false), RegleCompteursBarre.Filtrer(bruts, [Permissions.AchatsRegler], titulaire: false));
        Assert.Equal(new CompteursBarre(0, 0, 0, 0, 0, true), RegleCompteursBarre.Filtrer(bruts, [], titulaire: true));
    }
}
