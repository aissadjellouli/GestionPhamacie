using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

/// <summary>
/// Réglage de l'officine « facture et retour fournisseur laissés » (script 156) : lu par tout poste, changé par le titulaire seul,
/// deux valeurs seulement (jamais en clair), chaque changement inscrit au journal des actions avec l'avant et l'après.
/// </summary>
public sealed class ReglageTachesLaisseesTests
{
    private sealed class ReglageEnMemoire : IReglageTachesLaissees
    {
        public string Mode { get; set; } = ConservationTachesLaissees.Chiffre;
        public int Ecritures { get; private set; }
        public Task<string> LireAsync(CancellationToken ct = default) => Task.FromResult(Mode);
        public Task EnregistrerAsync(string mode, CancellationToken ct = default)
        {
            (Mode, Ecritures) = (mode, Ecritures + 1);
            return Task.CompletedTask;
        }
    }

    private static (ModifierReglageTachesLaissees, ReglageEnMemoire, JournalEspion) Fabrique(bool titulaire = true)
    {
        var (depot, journal) = (new ReglageEnMemoire(), new JournalEspion());
        // Droit « admin.parametres » sans le rôle de titulaire : ne suffit pas
        return (new ModifierReglageTachesLaissees(depot, new DroitsFactices("admin.parametres") { Titulaire = titulaire }, journal), depot, journal);
    }

    [Fact]
    public async Task Le_reglage_est_lu_tel_qu_enregistre_chiffre_par_defaut()
    {
        var depot = new ReglageEnMemoire();
        Assert.Equal("chiffre", await new ObtenirReglageTachesLaissees(depot).ExecuterAsync());
        depot.Mode = ConservationTachesLaissees.Desactive;
        Assert.Equal("desactive", await new ObtenirReglageTachesLaissees(depot).ExecuterAsync());
    }

    [Fact]
    public async Task Le_titulaire_desactive_et_le_journal_garde_avant_et_apres()
    {
        var (modifier, depot, journal) = Fabrique();

        await modifier.ExecuterAsync(" Desactive ", 1);

        Assert.Equal("desactive", depot.Mode);
        Assert.Equal("taches_laissees_poste", Assert.Single(journal.Actions));
        Assert.Equal("chiffre", journal.Details[0]["avant"]);
        Assert.Equal("desactive", journal.Details[0]["apres"]);
    }

    [Fact]
    public async Task Le_retour_a_chiffre_est_aussi_journalise()
    {
        var (modifier, depot, journal) = Fabrique();
        depot.Mode = ConservationTachesLaissees.Desactive;

        await modifier.ExecuterAsync("chiffre", 1);

        Assert.Equal("chiffre", depot.Mode);
        Assert.Equal("desactive", journal.Details.Single()["avant"]);
        Assert.Equal("chiffre", journal.Details.Single()["apres"]);
    }

    [Fact]
    public async Task Reserve_au_titulaire()
    {
        var (modifier, depot, journal) = Fabrique(titulaire: false);

        await Assert.ThrowsAsync<DroitRefuseException>(() => modifier.ExecuterAsync("desactive", 1));

        Assert.Equal(0, depot.Ecritures);
        Assert.Empty(journal.Actions);
    }

    [Theory]
    [InlineData("clair")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Seules_deux_valeurs_sont_permises_jamais_en_clair(string? mode)
    {
        var (modifier, depot, journal) = Fabrique();

        var e = await Assert.ThrowsAsync<RegleMetierException>(() => modifier.ExecuterAsync(mode, 1));

        Assert.Equal("taches_laissees_poste", e.Champ);
        Assert.Equal(0, depot.Ecritures);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public async Task Meme_valeur_rien_n_est_enregistre_ni_journalise()
    {
        var (modifier, depot, journal) = Fabrique();

        await modifier.ExecuterAsync("chiffre", 1);

        Assert.Equal(0, depot.Ecritures);
        Assert.Empty(journal.Actions);
    }

    [Fact]
    public void Le_changement_figure_dans_les_modifications_du_journal()
    {
        Assert.Contains(ModifierReglageTachesLaissees.ActionJournal, CategoriesJournal.Actions["modifications"]);
        Assert.Equal(["chiffre", "desactive"], ConservationTachesLaissees.Modes);
        // Mêmes valeurs côté poste (contrat HTTP)
        Assert.Equal(ConservationTachesLaissees.Chiffre, Pharma.Shared.ReglageTachesLaisseesDto.Chiffre);
        Assert.Equal(ConservationTachesLaissees.Desactive, Pharma.Shared.ReglageTachesLaisseesDto.Desactive);
    }
}
