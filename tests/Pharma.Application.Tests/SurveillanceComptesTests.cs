using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Application.Securite;
using Pharma.Application.Surveillance;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class JournalLectureEspion : IJournalLecture
{
    public FiltreJournal? Filtre { get; private set; }
    public Task<IReadOnlyList<EntreeJournal>> DernieresAsync(int nombre, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<EntreeJournal>>([]);
    public Task<Page<EntreeJournal>> PageAsync(FiltreJournal filtre, DemandePage page, CancellationToken ct = default)
    {
        Filtre = filtre;
        return Task.FromResult(new Page<EntreeJournal>([], 0, page.Page, page.Taille));
    }
}

public class SurveillanceComptesTests
{
    private static readonly HorlogeFixe Horloge = new(new DateOnly(2026, 11, 11));

    [Fact]
    public async Task Connexion_par_mot_de_passe_tracee_avec_le_poste_et_surveillee()
    {
        var comptes = new ComptesEnMemoire(new Utilisateur { Id = 7, Identifiant = "vendeur", NomComplet = "Vendeur Test", MotDePasseHash = "hash:secret" });
        var surveillance = new SurveillanceEnMemoire();
        await new SeConnecter(comptes, new VerificateurFactice(), new JetonsFactices(), surveillance: new Surveiller(surveillance))
            .ExecuterAsync("vendeur", "secret", default, " Caisse 2 ");
        Assert.Equal((7L, "Caisse 2", "mot_de_passe"), comptes.ConnexionsDetail.Single());
        Assert.Single(surveillance.Calculs);
    }

    [Fact]
    public async Task Patient_cree_garde_son_createur_et_journalise()
    {
        var (patients, journal) = (new PatientsEnMemoire(), new JournalEspion());
        var id = await new CreerPatient(patients, journal).ExecuterAsync(new NouveauPatient(" Benali ", "Amine", null, null, null, 2000m, null), 4);
        Assert.Equal(99, id);
        Assert.Equal(["patient_cree"], journal.Actions);
        Assert.Equal("Benali Amine", journal.Details.Single()["patient"]);
    }

    [Fact]
    public async Task Reglages_journalises_avec_ancienne_et_nouvelle_valeur_seulement_si_changes()
    {
        var (depot, journal) = (new SurveillanceEnMemoire(), new JournalEspion());
        var regler = new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, journal);
        await regler.EnregistrerAsync(depot.Parametres with { SeuilCreditsJour = 30000, SeuilBaissesPrix = 2 }, 1);
        var d = journal.Details.Single();
        Assert.Equal(["credits_jour", "baisses_prix"], d.Keys);
        Assert.Equal("{ avant = 20000, apres = 30000 }", d["credits_jour"]!.ToString());

        await regler.EnregistrerAsync(depot.Parametres, 1);              // rien de changé : rien au journal
        Assert.Single(journal.Actions);
    }

    [Theory]
    [InlineData(0, 7, 60, 10)]
    [InlineData(5000, 0, 60, 10)]
    [InlineData(5000, 7, 400, 10)]
    [InlineData(5000, 7, 60, 0)]
    public async Task Reglages_des_comptes_controles(decimal seuilCredit, int joursRecent, int joursJamais, int minutes)
    {
        var depot = new SurveillanceEnMemoire();
        await Assert.ThrowsAsync<RegleMetierException>(() => new ReglerSurveillance(depot, new DroitsFactices { Titulaire = true }, new JournalEspion())
            .EnregistrerAsync(depot.Parametres with
            {
                SeuilCreditNouveauPatient = seuilCredit, JoursPatientRecent = joursRecent, JoursCreditSansRemboursement = joursJamais,
                MinutesPostesSimultanes = minutes
            }, 1));
    }

    [Fact]
    public async Task Journal_filtre_par_categorie()
    {
        var lecture = new JournalLectureEspion();
        var consulter = new ConsulterJournal(lecture);
        await consulter.PageAsync(null, null, null, DemandePage.Lire(1, 10), default, "modifications");
        Assert.Contains("prix_modifie", lecture.Filtre!.Actions!);
        Assert.Contains("reglages_surveillance", lecture.Filtre.Actions!);
        await consulter.PageAsync(null, null, null, DemandePage.Lire(1, 10));
        Assert.Null(lecture.Filtre!.Actions);
        await Assert.ThrowsAsync<RegleMetierException>(() => consulter.PageAsync(null, null, null, DemandePage.Lire(1, 10), default, "inconnue"));
    }
}
