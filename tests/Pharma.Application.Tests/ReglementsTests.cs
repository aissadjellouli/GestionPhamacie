using Pharma.Application.Achats;
using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Achats;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class ReglementsEnMemoire(SituationGrossiste s) : IReglementsFournisseurs
{
    public ReglementAEcrire? Ecrit { get; private set; }
    public Task<IReadOnlyList<GrossisteAReglement>> GrossistesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GrossisteAReglement>>([]);
    public Task<SituationGrossiste?> SituationAsync(long id, CancellationToken ct = default) => Task.FromResult<SituationGrossiste?>(s);
    public Task<long> EnregistrerAsync(ReglementAEcrire r, CancellationToken ct = default) { Ecrit = r; return Task.FromResult(1L); }
    public Task<Pharma.Application.Commun.Page<MouvementGrossiste>> MouvementsAsync(long id, Pharma.Application.Commun.DemandePage page, Pharma.Application.Commun.FiltreReleve? filtre = null, CancellationToken ct = default) =>
        Task.FromResult(new Pharma.Application.Commun.Page<MouvementGrossiste>([], 0, page.Page, page.Taille));
}

internal sealed class CreditsEnMemoire(decimal solde) : ICreditsClients
{
    public (decimal Montant, string Mode, long Session)? Encaisse { get; private set; }
    public Task<IReadOnlyList<PatientEnCredit>> AvecCreditAsync(string? r, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatientEnCredit>>([]);
    public Task<Pharma.Application.Commun.Page<PatientEnCredit>> AvecCreditPageAsync(string? r, Pharma.Application.Commun.DemandePage p, CancellationToken ct = default) =>
        Task.FromResult(new Pharma.Application.Commun.Page<PatientEnCredit>([], 0, p.Page, p.Taille));
    public Task<SituationCredit?> SituationAsync(long id, CancellationToken ct = default) =>
        Task.FromResult<SituationCredit?>(new SituationCredit(new PatientEnCredit(id, "Patient fictif", null, solde, 5000, null), []));
    public JournalAEcrire? JournalEncaissement { get; private set; }
    public Task<string> EncaisserAsync(long p, decimal m, string mode, long s, long u, IReadOnlyList<long>? ventes = null, CancellationToken ct = default, JournalAEcrire? journal = null)
    { Encaisse = (m, mode, s); JournalEncaissement = journal; JournalEspion.Rejouer(journal is null ? null : [journal], new ContexteJournal(0, "RC-000007")); return Task.FromResult("RC-000007"); }
    public Task<Pharma.Application.Commun.Page<MouvementCredit>> MouvementsAsync(long p, Pharma.Application.Commun.DemandePage page, Pharma.Application.Commun.FiltreReleve? filtre = null, CancellationToken ct = default) =>
        Task.FromResult(new Pharma.Application.Commun.Page<MouvementCredit>([], 0, page.Page, page.Taille));
    public (decimal Avant, decimal Apres, string Motif)? Correction { get; private set; }
    public JournalAEcrire? JournalCorrection { get; private set; }
    public Task CorrigerAsync(long p, decimal avant, decimal apres, string motif, long u, string? poste, CancellationToken ct = default, JournalAEcrire? journal = null)
    { Correction = (avant, apres, motif); JournalCorrection = journal; JournalEspion.Rejouer(journal is null ? null : [journal], new ContexteJournal(0, "")); return Task.CompletedTask; }
}

public class ReglementsTests
{
    private static readonly SituationGrossiste Grossiste = new(4, "Grossiste fictif", 1200,
        [new FactureFournisseur(10, "F-1", "BL-1", new DateTime(2026, 10, 1), 500, 0, 500), new FactureFournisseur(11, "F-2", "BL-2", new DateTime(2026, 11, 1), 700, 0, 700)], []);

    private static EnregistrerReglement Cas(ReglementsEnMemoire stock, string droit = "achats.regler", SessionsEnMemoire? sessions = null) =>
        new(stock, sessions ?? new SessionsEnMemoire(), new DroitsFactices(droit), new HorlogeFixe(new DateOnly(2026, 11, 9)), new JournalEspion());

    [Fact]
    public async Task Sans_facture_choisie_les_plus_anciennes_sont_reglees()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        await Cas(stock).ExecuterAsync(4, null, 600, "cheque", "CHQ-001", "Banque fictive", null, 0, null, null, 1, "Caisse 1", compteId: 2);
        Assert.Equal([(10L, 500m), (11L, 100m)], stock.Ecrit!.Imputations);
        Assert.Null(stock.Ecrit.SessionCaisseId);
    }

    [Fact]
    public async Task Droit_requis_et_montant_au_plus_la_dette()
    {
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste), "achats.commander").ExecuterAsync(4, null, 100, "especes", null, null, null, 0, null, null, 1, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste)).ExecuterAsync(4, null, 1300, "especes", null, null, null, 0, null, null, 1, "Caisse 1"));
    }

    [Fact]
    public async Task Complement_du_tiroir_rattache_a_la_caisse_ouverte_du_poste()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        await Cas(stock).ExecuterAsync(4, null, 500, "especes", null, null, null, 200, null, [new(10, 500)], 1, "Caisse 1");
        Assert.Equal((200m, (long?)1), (stock.Ecrit!.PartTiroir, stock.Ecrit.SessionCaisseId));
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(new ReglementsEnMemoire(Grossiste), sessions: new SessionsEnMemoire { Ouverte = null })
                .ExecuterAsync(4, null, 500, "especes", null, null, null, 200, null, null, 1, "Caisse 1"));
    }

    [Fact]
    public async Task Credit_client_encaisse_dans_la_caisse_du_poste()
    {
        var stock = new CreditsEnMemoire(210);
        var recu = await new EncaisserReglementCredit(stock, new SessionsEnMemoire()).ExecuterAsync(3, 200, "especes", 5, "Caisse 1");
        Assert.Equal(("RC-000007", 10m), (recu.Numero, recu.ResteDu));
        Assert.Equal((200m, "especes", 1L), stock.Encaisse);
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            new EncaisserReglementCredit(new CreditsEnMemoire(210), new SessionsEnMemoire { Ouverte = null }).ExecuterAsync(3, 100, "especes", 5, "Caisse 1"));
    }

    private static EnregistrerReglement AvecTresorerie(ReglementsEnMemoire stock, TresorerieEnMemoire tresorerie) =>
        new(stock, new SessionsEnMemoire(), new DroitsFactices("achats.regler"), new HorlogeFixe(new DateOnly(2026, 11, 9)), new JournalEspion(), tresorerie);

    [Theory]
    [InlineData("cheque", "CHQ-1")]
    [InlineData("virement", "VIR-1")]
    [InlineData("prelevement", "PRL-1")]
    public async Task Cheque_virement_prelevement_exigent_une_banque_de_la_tresorerie(string moyen, string? reference)
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        var tresorerie = new TresorerieEnMemoire();
        var sansBanque = await Assert.ThrowsAsync<RegleMetierException>(() =>
            AvecTresorerie(stock, tresorerie).ExecuterAsync(4, null, 500, moyen, reference, null, null, 0, null, null, 1, "Caisse 1"));
        Assert.Equal("compte", sansBanque.Champ);
        // Le coffre n'est pas une banque
        await Assert.ThrowsAsync<RegleMetierException>(() =>
            AvecTresorerie(stock, tresorerie).ExecuterAsync(4, null, 500, moyen, reference, null, null, 0, null, null, 1, "Caisse 1", compteId: 1));
        Assert.Null(stock.Ecrit);

        await AvecTresorerie(stock, tresorerie).ExecuterAsync(4, null, 500, moyen, reference, null, null, 0, null, null, 1, "Caisse 1", compteId: 2);
        Assert.Equal((2L, moyen), (stock.Ecrit!.CompteId!.Value, stock.Ecrit.Moyen.Code()));
    }

    [Fact]
    public async Task Prelevement_exige_sa_reference()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => AvecTresorerie(stock, new TresorerieEnMemoire())
            .ExecuterAsync(4, null, 500, "prelevement", " ", null, null, 0, null, null, 1, "Caisse 1", compteId: 2));
        Assert.Equal("Indiquez la référence du prélèvement.", ex.Message);
        Assert.Null(stock.Ecrit);
    }

    [Fact]
    public async Task Traite_banque_facultative_numero_et_echeance_obligatoires()
    {
        var stock = new ReglementsEnMemoire(Grossiste);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => AvecTresorerie(stock, new TresorerieEnMemoire())
            .ExecuterAsync(4, null, 500, "effet", null, null, new DateOnly(2026, 12, 31), 0, null, null, 1, "Caisse 1"));
        Assert.Equal("Indiquez le n° de la traite.", ex.Message);
        await AvecTresorerie(stock, new TresorerieEnMemoire())
            .ExecuterAsync(4, null, 500, "effet", "TR-1", null, new DateOnly(2026, 12, 31), 0, null, null, 1, "Caisse 1");
        Assert.Null(stock.Ecrit!.CompteId);    // sans banque : payée à l'échéance sur confirmation du titulaire
        Assert.Equal(new DateOnly(2026, 12, 31), stock.Ecrit.Echeance);
    }
}

internal sealed class TraitesEnMemoire(params TraiteAPayer[] traites) : ITraites
{
    public List<PaiementTraiteAEcrire> Payees { get; } = [];
    public FiltreTraites? Filtre { get; private set; }
    /// <summary>État « a_payer » seulement (les autres états sont filtrés par la requête SQL) ; texte : grossiste ou n°.</summary>
    public Task<Page<TraiteAPayer>> ListerAsync(FiltreTraites filtre, DemandePage page, CancellationToken ct = default)
    {
        Filtre = filtre;
        var retenues = traites.Where(t => t.Echeance <= filtre.Aujourdhui.AddDays(ConsulterTraites.JoursAvantEcheance)
                                          && Payees.All(p => p.ReglementId != t.ReglementId)
                                          && (filtre.Texte is null || t.Grossiste.Contains(filtre.Texte) || (t.Reference ?? "").Contains(filtre.Texte)))
            .ToList();
        return Task.FromResult(new Page<TraiteAPayer>(retenues.Skip(page.Decalage).Take(page.Taille).ToList(), retenues.Count, page.Page, page.Taille));
    }
    public Task<TraiteAPayer?> TraiteAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(traites.FirstOrDefault(t => t.ReglementId == id && Payees.All(p => p.ReglementId != id)));
    public Task<long> PayerAsync(PaiementTraiteAEcrire p, CancellationToken ct = default) { Payees.Add(p); return Task.FromResult(30L); }
}

public class TraitesTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);
    private static readonly TraiteAPayer SansBanque = new(20, "RG-000020", 4, "Grossiste fictif", "TR-20", null, Jour.AddDays(-60), Jour.AddDays(-1), 3000, null, null);
    private static readonly TraiteAPayer AvecBanque = new(21, "RG-000021", 4, "Grossiste fictif", "TR-21", null, Jour.AddDays(-60), Jour.AddDays(3), 2000, 2, "Banque fictive");
    private static readonly TraiteAPayer Lointaine = new(22, "RG-000022", 4, "Grossiste fictif", "TR-22", null, Jour.AddDays(-10), Jour.AddDays(30), 1000, null, null);

    private static PayerTraite Cas(TraitesEnMemoire traites, JournalEspion journal, bool titulaire = true, SessionsEnMemoire? sessions = null) =>
        new(traites, sessions ?? new SessionsEnMemoire { Especes = 5000 }, new DroitsFactices("achats.regler") { Titulaire = titulaire }, new HorlogeFixe(Jour),
            journal, new TresorerieEnMemoire());

    [Fact]
    public async Task Liste_des_traites_echues_ou_a_sept_jours_avec_le_coffre_pour_le_titulaire()
    {
        var traites = new TraitesEnMemoire(SansBanque, AvecBanque, Lointaine);
        var droits = new DroitsFactices("achats.regler") { Titulaire = true };
        var r = await new ConsulterTraites(traites, droits, new HorlogeFixe(Jour), new TresorerieEnMemoire()).ExecuterAsync(1);
        Assert.Equal([20L, 21L], r.Traites.Lignes.Select(t => t.ReglementId));
        Assert.Equal(("a_payer", Jour), (traites.Filtre!.Etat, traites.Filtre.Aujourdhui));   // état par défaut, date du serveur
        Assert.Equal(10000m, r.SoldeCoffre);
        var employe = await new ConsulterTraites(traites, new DroitsFactices("achats.regler"), new HorlogeFixe(Jour), new TresorerieEnMemoire()).ExecuterAsync(1);
        Assert.Null(employe.SoldeCoffre);
    }

    [Fact]
    public async Task Filtres_des_traites_lus_et_bornes_par_le_serveur()
    {
        var traites = new TraitesEnMemoire(SansBanque, AvecBanque, Lointaine);
        var cas = new ConsulterTraites(traites, new DroitsFactices("achats.regler"), new HorlogeFixe(Jour));
        var r = await cas.ExecuterAsync(1, "payees", "  TR-2  ", new DemandePage(1, 5));
        Assert.Equal(("payees", "TR-2"), (traites.Filtre!.Etat, traites.Filtre.Texte));
        await cas.ExecuterAsync(1, "inconnu", " ");
        Assert.Equal(("a_payer", (string?)null), (traites.Filtre!.Etat, traites.Filtre.Texte));   // état inconnu : par défaut
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterTraites(traites, new DroitsFactices("stock.consulter"), new HorlogeFixe(Jour)).ExecuterAsync(1));
    }

    [Fact]
    public async Task Paiement_reserve_au_titulaire()
    {
        var traites = new TraitesEnMemoire(SansBanque);
        await Assert.ThrowsAsync<DroitRefuseException>(() => Cas(traites, new JournalEspion(), titulaire: false)
            .ExecuterAsync(20, null, "especes", null, null, 0, 1, "Caisse 1"));
        Assert.Empty(traites.Payees);
    }

    [Fact]
    public async Task Traite_payee_par_cheque_virement_ou_prelevement_exige_une_banque_et_est_journalisee()
    {
        var traites = new TraitesEnMemoire(SansBanque);
        var journal = new JournalEspion();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => Cas(traites, journal).ExecuterAsync(20, null, "virement", null, "VIR-9", 0, 1, "Caisse 1"));
        Assert.Equal("compte", ex.Champ);
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(traites, journal).ExecuterAsync(20, null, "effet", 2, null, 0, 1, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(traites, journal).ExecuterAsync(20, null, "cheque", 2, "CHQ-9", 100, 1, "Caisse 1"));   // tiroir : espèces seulement
        // Référence obligatoire hors espèces
        Assert.Equal("Indiquez le n° du chèque.", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(traites, journal).ExecuterAsync(20, null, "cheque", 2, null, 0, 1, "Caisse 1"))).Message);
        Assert.Equal("Indiquez la référence du virement.", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(traites, journal).ExecuterAsync(20, null, "virement", 2, " ", 0, 1, "Caisse 1"))).Message);
        Assert.Equal("Indiquez la référence du prélèvement.", (await Assert.ThrowsAsync<RegleMetierException>(() =>
            Cas(traites, journal).ExecuterAsync(20, null, "prelevement", 2, null, 0, 1, "Caisse 1"))).Message);
        Assert.Empty(traites.Payees);

        await Cas(traites, journal).ExecuterAsync(20, null, "prelevement", 2, "PRL-20", 0, 1, "Caisse 1");
        var p = Assert.Single(traites.Payees);
        Assert.Equal(("Prelevement", (long?)2, Jour, 0m, "PRL-20"), (p.Moyen.ToString(), p.CompteId, p.Date, p.PartTiroir, p.Reference));
        Assert.Equal(["traite_payee"], journal.Actions);
        // Déjà payée : plus proposée
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(traites, journal).ExecuterAsync(20, null, "prelevement", 2, "PRL-20", 0, 1, "Caisse 1"));
    }

    [Fact]
    public async Task Especes_complement_du_tiroir_pris_dans_la_caisse_ouverte_du_poste()
    {
        var traites = new TraitesEnMemoire(SansBanque);
        await Cas(traites, new JournalEspion()).ExecuterAsync(20, null, "especes", 99, null, 1200, 1, "Caisse 1");
        var p = Assert.Single(traites.Payees);
        Assert.Equal((1200m, (long?)1, (long?)null), (p.PartTiroir, p.SessionCaisseId, p.CompteId));   // espèces : pas de banque

        // Complément supérieur à la traite, ou caisse du poste fermée : refus
        var autre = new TraitesEnMemoire(SansBanque);
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(autre, new JournalEspion()).ExecuterAsync(20, null, "especes", null, null, 3000.5m, 1, "Caisse 1"));
        await Assert.ThrowsAsync<RegleMetierException>(() => Cas(autre, new JournalEspion(), sessions: new SessionsEnMemoire { Ouverte = null })
            .ExecuterAsync(20, null, "especes", null, null, 500, 1, "Caisse 1"));
        Assert.Empty(autre.Payees);
    }

    [Fact]
    public async Task Traite_remise_avec_une_banque_confirmee_sans_second_debit()
    {
        var traites = new TraitesEnMemoire(AvecBanque);
        await Cas(traites, new JournalEspion()).ExecuterAsync(21, null, "especes", null, null, 500, 1, "Caisse 1");
        var p = Assert.Single(traites.Payees);
        // Moyen et tiroir ignorés : la banque de la remise, rien pris dans le tiroir (le débit à l'échéance reste le seul)
        Assert.Equal(((long?)2, 0m, (long?)null), (p.CompteId, p.PartTiroir, p.SessionCaisseId));
    }
}
