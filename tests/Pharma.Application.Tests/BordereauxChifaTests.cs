using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Bordereaux en mémoire : montants et statut calculés comme v_bordereau et RegleBordereau.StatutApres (script 110).</summary>
internal sealed class BordereauxEnMemoire : IBordereaux
{
    public sealed class B
    {
        public long Id, OrganismeId;
        public string Canal = "chifa", Numero = "", Statut = RegleBordereau.Preparation;
        public long? CentreId;
        public decimal AttenduFixe, Verse;
        public decimal? Declare;
        public DateOnly? Depot;
        public List<long> Ventes = [];
    }

    public sealed class Lien
    {
        public long BordereauId, VenteId;
        public DateTime? RejeteLe;
        public decimal MontantRejete;
        public string? Motif, Suite;
    }

    public List<B> Liste { get; } = [];
    public List<Lien> Liens { get; } = [];
    public List<VenteRattachable> Ventes { get; } = [];
    public Dictionary<long, decimal> Dettes { get; } = [];

    public void Ajouter(long id, long organisme = 1, string statut = "depose", decimal attendu = 0, decimal verse = 0, long? centre = 3) =>
        Liste.Add(new B { Id = id, OrganismeId = organisme, Statut = statut, AttenduFixe = attendu, Verse = verse, Numero = $"B{id}", CentreId = centre });

    public VenteRattachable Vente(long id, decimal part = 800, long? centre = 3, string canal = "chifa", long organisme = 1, string? facture = "06931126",
        long? patient = 7)
    {
        var v = new VenteRattachable(id, $"T-{id:000000}", canal, "validee", organisme, centre, false, false, facture, false, part, patient);
        Ventes.Add(v);
        return v;
    }

    private decimal Part(long venteId) => Ventes.First(v => v.Id == venteId).PartOrganisme;

    private Bordereau Lire(B b)
    {
        var liens = Liens.Where(l => l.BordereauId == b.Id).ToList();
        var attendu = liens.Count > 0 ? liens.Sum(l => Part(l.VenteId)) : b.AttenduFixe;
        var rejete = liens.Sum(l => l.MontantRejete);
        return new Bordereau(b.Id, b.OrganismeId, b.OrganismeId == 9 ? "MDN" : "CNAS", b.Canal, b.CentreId, null, b.Numero, new DateOnly(2026, 10, 1),
            new DateOnly(2026, 10, 31), b.Depot, b.Statut, b.Declare, liens.Count, attendu, rejete, liens.Count(l => l.RejeteLe is not null && l.Suite is null),
            b.Verse, attendu - b.Verse - rejete, DateTime.UtcNow, "Titulaire");
    }

    private void Recalculer(B b)
    {
        var x = Lire(b);
        b.Statut = RegleBordereau.StatutApres(b.Statut, x.Attendu, x.Verse, x.Rejete, x.RejetsATraiter);
    }

    /// <summary>Versement imputé (comme TresorerieSql) : statut recalculé.</summary>
    public void Payer(long id, decimal montant)
    {
        var b = Liste.First(x => x.Id == id);
        b.Verse += montant;
        Recalculer(b);
    }

    public Task<Page<Bordereau>> ListerAsync(FiltreBordereaux f, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(PageEnMemoire.Paginer(Liste.Select(Lire).ToList(), page));
    public Task<Bordereau?> LireAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Liste.FirstOrDefault(b => b.Id == id) is { } b ? Lire(b) : null);
    public Task<IReadOnlyList<LigneBordereau>> LignesAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<LigneBordereau>>(Liens.Where(l => l.BordereauId == id).Select(l =>
        {
            var v = Ventes.First(x => x.Id == l.VenteId);
            return new LigneBordereau(v.Id, v.Numero, DateTime.UtcNow, v.PatientId, v.PatientId is null ? null : "Patient", v.FactureChifa, v.PartOrganisme,
                DateTime.UtcNow, l.RejeteLe, l.Motif, l.MontantRejete, l.Suite, null, null);
        }).ToList());
    public Task<IReadOnlyList<VersementBordereau>> VersementsAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VersementBordereau>>([]);
    public Task<IReadOnlyList<HistoriqueBordereau>> HistoriqueAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HistoriqueBordereau>>([]);

    private bool Libre(VenteRattachable v) => !Liens.Any(l => l.VenteId == v.Id && l.Suite != RegleBordereau.Redeposer);

    public Task<Page<VenteADeposer>> VentesADeposerAsync(FiltreVentesADeposer f, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(PageEnMemoire.Paginer(Ventes.Where(Libre).Select(v => new VenteADeposer(v.Id, v.Numero, DateTime.UtcNow, v.OrganismeId ?? 0, "CNAS",
            v.Canal, v.CentrePayeurId, null, v.PatientId, null, v.FactureChifa, v.PartOrganisme, Liens.Any(l => l.VenteId == v.Id), false)).ToList(), page));
    public Task<IReadOnlyList<long>> IdsVentesADeposerAsync(FiltreVentesADeposer f, int max, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<long>>(Ventes.Where(Libre)
            .Where(v => v.OrganismeId == f.OrganismeId && (f.CentrePayeurId is null || v.CentrePayeurId == f.CentrePayeurId || f.AvecSansCentre && v.CentrePayeurId is null))
            .Select(v => v.Id).Take(max + 1).ToList());
    public Task<VenteRattachable?> VenteAsync(long venteId, CancellationToken ct = default) =>
        Task.FromResult(Ventes.FirstOrDefault(v => v.Id == venteId) is { } v
            ? v with
            {
                SurUnBordereau = Liens.Any(l => l.VenteId == v.Id && l.Suite is null),
                RejeteeDefinitivement = Liens.Any(l => l.VenteId == v.Id && l.Suite is RegleBordereau.Perte or RegleBordereau.Patient)
            }
            : null);
    public Task<long?> VenteParFactureAsync(string facture, CancellationToken ct = default) =>
        Task.FromResult(Ventes.FirstOrDefault(v => v.FactureChifa == facture)?.Id);
    public Task<CompteursBordereaux> CompteursAsync(CancellationToken ct = default) => Task.FromResult(new CompteursBordereaux(0, 0, 0, 0, 0, 0));

    public Task<long> CreerAsync(BordereauAEcrire b, long u, CancellationToken ct = default)
    {
        if (Liste.Any(x => x.OrganismeId == b.OrganismeId && x.Numero == b.Numero))
            throw new RegleMetierException($"Le bordereau n° {b.Numero} existe déjà pour cet organisme.", "numero");
        var id = Liste.Count == 0 ? 1 : Liste.Max(x => x.Id) + 1;
        Liste.Add(new B { Id = id, OrganismeId = b.OrganismeId, Canal = b.Canal, CentreId = b.CentrePayeurId, Numero = b.Numero, Declare = b.MontantDeclare });
        return Task.FromResult(id);
    }

    public Task<bool> ModifierAsync(long id, string numero, DateOnly d, DateOnly f, decimal? declare, CancellationToken ct = default)
    {
        var b = Liste.First(x => x.Id == id);
        if (b.Statut != RegleBordereau.Preparation) return Task.FromResult(false);
        (b.Numero, b.Declare) = (numero, declare);
        return Task.FromResult(true);
    }

    public Task<int> AjouterVentesAsync(long bordereauId, IReadOnlyList<long> ventes, long u, CancellationToken ct = default)
    {
        var n = 0;
        foreach (var id in ventes)
        {
            if (!Libre(Ventes.First(v => v.Id == id))) continue;
            Liens.Add(new Lien { BordereauId = bordereauId, VenteId = id });
            n++;
        }
        return Task.FromResult(n);
    }

    public Task<bool> RetirerVenteAsync(long bordereauId, long venteId, CancellationToken ct = default) =>
        Task.FromResult(Liens.RemoveAll(l => l.BordereauId == bordereauId && l.VenteId == venteId && l.RejeteLe is null) == 1);

    public Task<bool> DeposerAsync(long id, DateOnly date, long u, CancellationToken ct = default)
    {
        var b = Liste.First(x => x.Id == id);
        if (b.Statut != RegleBordereau.Preparation) return Task.FromResult(false);
        (b.Statut, b.Depot) = (RegleBordereau.Depose, date);
        return Task.FromResult(true);
    }

    public Task<bool> RejeterAsync(long bordereauId, long venteId, string motif, string? texte, decimal montant, long u, CancellationToken ct = default)
    {
        var l = Liens.First(x => x.BordereauId == bordereauId && x.VenteId == venteId);
        if (l.RejeteLe is not null) return Task.FromResult(false);
        (l.RejeteLe, l.Motif, l.MontantRejete) = (DateTime.UtcNow, texte ?? motif, montant);
        Recalculer(Liste.First(x => x.Id == bordereauId));
        return Task.FromResult(true);
    }

    public Task<bool> TraiterRejetAsync(long bordereauId, long venteId, string suite, long u, string reference, CancellationToken ct = default)
    {
        var l = Liens.First(x => x.BordereauId == bordereauId && x.VenteId == venteId);
        if (l.RejeteLe is null || l.Suite is not null) return Task.FromResult(false);
        l.Suite = suite;
        if (suite == RegleBordereau.Patient)
        {
            var p = Ventes.First(v => v.Id == venteId).PatientId!.Value;
            Dettes[p] = Dettes.GetValueOrDefault(p) + l.MontantRejete;
        }
        Recalculer(Liste.First(x => x.Id == bordereauId));
        return Task.FromResult(true);
    }
}

/// <summary>Bordereaux Chifa / MDN (2026-11-15, script 110).</summary>
public class BordereauxChifaTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 15));

    private static (GererBordereaux Cas, BordereauxEnMemoire B, JournalEspion J) Monter(params string[] droits)
    {
        var b = new BordereauxEnMemoire();
        var j = new JournalEspion();
        return (new GererBordereaux(b, new CentresEnMemoire(), new DroitsFactices(droits.Length == 0 ? ["chifa.bordereaux"] : droits), j, Horloge), b, j);
    }

    private static async Task<long> Bordereau(GererBordereaux cas, string numero = "100126", long organisme = 1, long? centre = 3) =>
        (await cas.CreerAsync(organisme, centre, numero, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, 5)).Id;

    [Fact]
    public async Task Creation_numero_saisi_unique_par_organisme_centre_obligatoire_sauf_MDN_et_droit_exige()
    {
        var (cas, b, j) = Monter();
        var id = await Bordereau(cas);
        Assert.Equal(("100126", RegleBordereau.Preparation, "chifa"), (b.Liste[0].Numero, b.Liste[0].Statut, b.Liste[0].Canal));
        Assert.Contains("bordereau_cree", j.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => Bordereau(cas));                         // même n°, même organisme
        await Bordereau(cas, organisme: 2, centre: 4);                                                 // même n°, autre organisme : permis
        await Assert.ThrowsAsync<RegleMetierException>(() => Bordereau(cas, "200", centre: null));    // CNAS sans centre
        await Assert.ThrowsAsync<RegleMetierException>(() => Bordereau(cas, "201", centre: 4));       // centre d'un autre organisme
        var mdn = await Bordereau(cas, "M-1", organisme: 9, centre: null);                            // MDN : centre facultatif
        Assert.Equal("mdn", b.Liste.First(x => x.Id == mdn).Canal);
        await Assert.ThrowsAsync<RegleMetierException>(() => Bordereau(cas, " "));
        Assert.True(id > 0);
        await Assert.ThrowsAsync<DroitRefuseException>(() => Bordereau(Monter("caisse.vendre").Cas, "300"));
    }

    [Fact]
    public async Task Une_vente_sur_un_seul_bordereau_et_montant_attendu_somme_des_parts()
    {
        var (cas, b, _) = Monter();
        b.Vente(10, 928.66m);
        b.Vente(11, 500m, centre: null);                // sans centre : acceptée, le centre du bordereau lui est donné
        b.Vente(12, 300m, centre: 5);                   // autre centre
        b.Vente(13, 200m, facture: null);               // sans n° de facture Chifa
        var b1 = await Bordereau(cas);
        var b2 = await Bordereau(cas, "100127");

        Assert.Equal(2, await cas.AjouterVentesAsync(b1, [10, 11], null, 5));
        Assert.Equal(1428.66m, (await cas.FicheAsync(b1, 5)).Bordereau.Attendu);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterVentesAsync(b2, [10], null, 5));
        Assert.Contains("déjà sur un bordereau", e.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterVentesAsync(b2, [12], null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterVentesAsync(b2, [13], null, 5));
        // Par le n° de facture scanné
        b.Vente(14, 100m, facture: "07000001");
        Assert.Equal("T-000014", await cas.AjouterParFactureAsync(b2, " 0700-0001 ", 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterParFactureAsync(b2, "99999999", 5));
        // Retirée d'un bordereau en préparation : de nouveau libre
        await cas.RetirerVenteAsync(b1, 11, 5);
        Assert.Equal(1, await cas.AjouterVentesAsync(b2, [11], null, 5));
    }

    [Fact]
    public async Task Toutes_les_ventes_du_filtre_du_centre_et_sans_centre()
    {
        var (cas, b, _) = Monter();
        b.Vente(20); b.Vente(21, centre: null); b.Vente(22, centre: 5); b.Vente(23, organisme: 2, centre: 4);
        var id = await Bordereau(cas);
        Assert.Equal(2, await cas.AjouterVentesAsync(id, null, new FiltreVentesADeposer(), 5));
        Assert.Equal([20L, 21L], b.Liens.Select(l => l.VenteId).Order());
    }

    [Fact]
    public async Task Ecart_entre_montant_declare_et_attendu()
    {
        var (cas, b, _) = Monter();
        b.Vente(30, 928.66m);
        var id = await Bordereau(cas);
        await cas.AjouterVentesAsync(id, [30], null, 5);
        Assert.Null((await cas.FicheAsync(id, 5)).Bordereau.Ecart);
        await cas.ModifierAsync(id, "100126", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 1000m, 5);
        Assert.Equal(71.34m, (await cas.FicheAsync(id, 5)).Bordereau.Ecart);
        await cas.ModifierAsync(id, "100126", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 928.66m, 5);
        Assert.Equal(0m, (await cas.FicheAsync(id, 5)).Bordereau.Ecart);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(id, "100126", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), -1m, 5));
    }

    [Fact]
    public async Task Statuts_preparation_depot_puis_ventes_figees()
    {
        var (cas, b, j) = Monter();
        b.Vente(40); b.Vente(41);
        var id = await Bordereau(cas);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.DeposerAsync(id, null, 5));             // vide
        await cas.AjouterVentesAsync(id, [40], null, 5);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.DeposerAsync(id, new DateOnly(2026, 11, 16), 5));   // futur
        await cas.DeposerAsync(id, null, 5);
        Assert.Equal((RegleBordereau.Depose, new DateOnly(2026, 11, 15)), (b.Liste[0].Statut, b.Liste[0].Depot!.Value));
        Assert.Contains("bordereau_depose", j.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterVentesAsync(id, [41], null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RetirerVenteAsync(id, 40, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierAsync(id, "X", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.DeposerAsync(id, null, 5));
    }

    private static async Task<(GererBordereaux Cas, BordereauxEnMemoire B, long Id)> Depose(params (long Id, decimal Part, long? Patient)[] ventes)
    {
        var (cas, b, _) = Monter();
        foreach (var v in ventes) b.Vente(v.Id, v.Part, patient: v.Patient);
        var id = await Bordereau(cas);
        await cas.AjouterVentesAsync(id, ventes.Select(v => v.Id).ToList(), null, 5);
        await cas.DeposerAsync(id, null, 5);
        return (cas, b, id);
    }

    [Fact]
    public async Task Rejet_motif_et_montant_au_plus_la_part_organisme()
    {
        var (cas, b, id) = await Depose((50, 800m, 7));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RejeterAsync(id, 50, "droits_expires", null, 800.01m, null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RejeterAsync(id, 50, "droits_expires", null, 0, null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RejeterAsync(id, 50, "autre", " ", 100, null, 5));
        await cas.RejeterAsync(id, 50, "droits_expires", null, 800m, null, 5);
        var f = await cas.FicheAsync(id, 5);
        Assert.Equal((800m, 1, 0m), (f.Bordereau.Rejete, f.Bordereau.RejetsATraiter, f.Bordereau.Reste));
        Assert.Equal(RegleBordereau.Paye, b.Liste[0].Statut);          // plus rien à recevoir, mais un rejet à traiter
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RejeterAsync(id, 50, "droits_expires", null, 100, null, 5));   // déjà rejetée
    }

    [Fact]
    public async Task Rejet_suite_redeposer_la_vente_redevient_a_deposer()
    {
        var (cas, b, id) = await Depose((60, 800m, 7), (61, 200m, 7));
        await cas.RejeterAsync(id, 60, "erreur_saisie", null, 800m, RegleBordereau.Redeposer, 5);
        Assert.Contains(60L, (await cas.VentesADeposerAsync(new FiltreVentesADeposer(1), new DemandePage(), 5)).Lignes.Select(v => v.VenteId));
        var autre = await Bordereau(cas, "100200");
        Assert.Equal(1, await cas.AjouterVentesAsync(autre, [60], null, 5));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.TraiterRejetAsync(id, 60, RegleBordereau.Perte, 5));   // déjà traité
        Assert.Equal(RegleBordereau.Depose, b.Liste[0].Statut);         // 200 encore à recevoir
    }

    [Fact]
    public async Task Rejet_suite_perte_puis_ne_se_redepose_plus()
    {
        var (cas, b, id) = await Depose((70, 500m, 7));
        await cas.RejeterAsync(id, 70, "produit_non_remboursable", null, 500m, null, 5);
        await cas.TraiterRejetAsync(id, 70, RegleBordereau.Perte, 5);
        Assert.Equal(RegleBordereau.Solde, b.Liste[0].Statut);
        var autre = await Bordereau(cas, "100300");
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.AjouterVentesAsync(autre, [70], null, 5));
    }

    [Fact]
    public async Task Rejet_suite_reclamer_au_patient_ajoute_a_sa_dette()
    {
        var (cas, b, id) = await Depose((80, 300m, 7), (81, 100m, null));
        await cas.RejeterAsync(id, 80, "droits_expires", null, 120m, RegleBordereau.Patient, 5);
        Assert.Equal(120m, b.Dettes[7]);
        await cas.RejeterAsync(id, 81, "droits_expires", null, 100m, null, 5);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.TraiterRejetAsync(id, 81, RegleBordereau.Patient, 5));   // vente sans patient
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.TraiterRejetAsync(id, 81, "inconnue", 5));
    }

    [Fact]
    public async Task Versement_partiel_puis_solde()
    {
        var (cas, b, id) = await Depose((90, 600m, 7), (91, 400m, 7));
        b.Payer(id, 500m);
        Assert.Equal(RegleBordereau.PayePartiel, b.Liste[0].Statut);
        await cas.RejeterAsync(id, 91, "droits_expires", null, 100m, RegleBordereau.Perte, 5);
        Assert.Equal(RegleBordereau.PayePartiel, b.Liste[0].Statut);   // 1000 − 500 − 100 = 400 encore dus
        b.Payer(id, 400m);
        Assert.Equal(RegleBordereau.Solde, b.Liste[0].Statut);
        Assert.Equal(0m, (await cas.FicheAsync(id, 5)).Bordereau.Reste);
    }
}
