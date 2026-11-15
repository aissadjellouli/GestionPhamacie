using Pharma.Application.Commun;
using Pharma.Application.Reprise;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class DoublonsImportEnMemoire : IDoublonsImport
{
    public List<DoublonPatientImport> Doublons { get; } = [];
    public List<(long Id, DecisionDoublon Decision, DetteDoublon? Dette, string? Num)> Decisions { get; } = [];
    public FiltreDoublonsImport? DernierFiltre { get; private set; }

    public Task<Page<DoublonPatientImport>> ListerAsync(FiltreDoublonsImport filtre, DemandePage page, CancellationToken ct = default)
    {
        DernierFiltre = filtre;
        return Task.FromResult(PageEnMemoire.Paginer(Doublons, page));
    }
    public Task<DoublonPatientImport?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Doublons.FirstOrDefault(d => d.Id == id));
    public Task<long> DeciderAsync(long id, DecisionDoublon decision, DetteDoublon? dette, string? num, long u, CancellationToken ct = default)
    {
        Decisions.Add((id, decision, dette, num));
        return Task.FromResult(decision == DecisionDoublon.AyantDroit ? 99L : Doublons.First(d => d.Id == id).PatientExistantId);
    }
}

/// <summary>Règle du titulaire 2026-11-15 (choix « a ») : doublons de n° d'assuré de la reprise, à valider (script 092).</summary>
public class DoublonsImportTests
{
    // Doublon dans le fichier (ligne 2, même n° que la ligne 1, avec une dette) et doublon avec la base (sans dette)
    private static DoublonPatientImport Fichier => new(1, 7, "Ancien 3.2", "B", 2, "BENALI", "Karim", null, "690755003842-80", 1500m, "fichier",
        10, "BENALI Karim", "690755003842/80", null, 200m, "a_valider", null, null, null, null);
    private static DoublonPatientImport Base => new(2, 7, "Ancien 3.2", "D", 4, "SAIDI", "Lina", null, "550101000001/00", 0m, "base",
        11, "SAIDI Lina", "550101000001/00", null, 0m, "a_valider", null, null, null, null);

    private static (DeciderDoublonImport Cas, DoublonsImportEnMemoire Depot, JournalEspion Journal) Cas(DroitsFactices droits, params DoublonPatientImport[] d)
    {
        var depot = new DoublonsImportEnMemoire();
        depot.Doublons.AddRange(d);
        var journal = new JournalEspion();
        return (new DeciderDoublonImport(depot, droits, journal), depot, journal);
    }

    [Fact]
    public async Task Doublon_du_fichier_garder_l_existant_avec_dette_rattachee_est_journalise()
    {
        var (cas, depot, journal) = Cas(new DroitsFactices { Titulaire = true }, Fichier);
        var patient = await cas.ExecuterAsync(1, DecisionDoublon.GarderExistant, DetteDoublon.Rattacher, "ignoré", 5);
        Assert.Equal(10, patient);
        Assert.Equal((1L, DecisionDoublon.GarderExistant, (DetteDoublon?)DetteDoublon.Rattacher, (string?)null), depot.Decisions.Single());
        Assert.Equal("reprise_doublon_patient", journal.Actions.Single());
        Assert.Equal("garder_existant", journal.Details[0]["decision"]);
        Assert.Equal("rattachee", journal.Details[0]["dette"]);
        Assert.Equal(1500m, journal.Details[0]["montant_dette"]);
    }

    [Fact]
    public async Task Dette_reprise_sans_choix_est_refusee()
    {
        var (cas, depot, _) = Cas(new DroitsFactices { Titulaire = true }, Fichier);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(1, DecisionDoublon.Remplacer, null, null, 5));
        Assert.Contains("rattacher ou de l’abandonner", ex.Message);
        Assert.Empty(depot.Decisions);
    }

    [Fact]
    public async Task Doublon_avec_la_base_sans_dette_remplacer_ne_transmet_pas_de_choix_de_dette()
    {
        var (cas, depot, journal) = Cas(new DroitsFactices("reprise.doublons"), Base);
        await cas.ExecuterAsync(2, DecisionDoublon.Remplacer, DetteDoublon.Abandonner, null, 5);
        Assert.Null(depot.Decisions.Single().Dette);
        Assert.Null(journal.Details[0]["dette"]);
    }

    [Fact]
    public async Task Ayant_droit_exige_un_autre_suffixe()
    {
        var (cas, depot, _) = Cas(new DroitsFactices { Titulaire = true }, Base);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, DecisionDoublon.AyantDroit, null, " ", 5));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, DecisionDoublon.AyantDroit, null, "550101000001-00", 5));
        Assert.Contains("changez le suffixe", ex.Message);
        Assert.Equal(99, await cas.ExecuterAsync(2, DecisionDoublon.AyantDroit, null, " 550101000001/01 ", 5));
        Assert.Equal("550101000001/01", depot.Decisions.Single().Num);
    }

    [Fact]
    public async Task Doublon_deja_traite_est_refuse()
    {
        var (cas, depot, _) = Cas(new DroitsFactices { Titulaire = true }, Base with { Statut = "remplace", DecideLe = DateTime.UtcNow });
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(2, DecisionDoublon.GarderExistant, null, null, 5));
        Assert.Empty(depot.Decisions);
    }

    [Fact]
    public async Task Sans_titulaire_ni_droit_reprise_doublons_la_decision_et_la_liste_sont_refusees_meme_avec_credit_accorder()
    {
        var droits = new DroitsFactices("caisse.vendre", "credit.accorder");
        var (cas, depot, _) = Cas(droits, Base);
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(2, DecisionDoublon.GarderExistant, null, null, 5));
        await Assert.ThrowsAsync<DroitRefuseException>(() => new ListerDoublonsImport(depot, droits).ExecuterAsync(new(), new(), 5));
        Assert.Empty(depot.Decisions);
    }

    [Fact]
    public async Task Avec_le_droit_reprise_doublons_un_employe_liste_et_decide()
    {
        var droits = new DroitsFactices(Pharma.Domain.Securite.Permissions.RepriseDoublons);
        var (cas, depot, _) = Cas(droits, Base);
        await new ListerDoublonsImport(depot, droits).ExecuterAsync(new(), new(), 5);
        await cas.ExecuterAsync(2, DecisionDoublon.GarderExistant, null, null, 5);
        Assert.Single(depot.Decisions);
    }

    [Fact]
    public void Le_droit_reprise_doublons_n_est_pas_autorisable_par_code_comptoir()
    {
        Assert.Equal("reprise.doublons", Pharma.Domain.Securite.Permissions.RepriseDoublons);
        Assert.False(Pharma.Domain.Securite.CodeComptoir.AutorisableParCollegue(Pharma.Domain.Securite.Permissions.RepriseDoublons));
    }

    [Fact]
    public async Task Liste_filtree_par_statut_et_texte()
    {
        var depot = new DoublonsImportEnMemoire();
        depot.Doublons.AddRange([Fichier, Base]);
        var lister = new ListerDoublonsImport(depot, new DroitsFactices("reprise.doublons"));
        var page = await lister.ExecuterAsync(new FiltreDoublonsImport(" a_valider ", "  saidi "), DemandePage.Lire(1, 10), 5);
        Assert.Equal(new FiltreDoublonsImport("a_valider", "saidi"), depot.DernierFiltre);
        Assert.Equal(2, page.Total);
        await Assert.ThrowsAsync<RegleMetierException>(() => lister.ExecuterAsync(new FiltreDoublonsImport("supprime"), new(), 5));
    }
}
