using Pharma.Application.Caisse;
using Pharma.Application.Patients;
using Pharma.Domain.Ventes;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>Rang de l'assuré (suffixe « /NN ») et fin de droits Chifa (script 102, 2026-11-15, D-4).</summary>
public class RangFinDroitTests
{
    [Theory]
    [InlineData("690755008888/02", "02")]
    [InlineData("690755007777/80", "80")]
    [InlineData("690755003842 / 01 ", "01")]
    [InlineData("690755003842", null)]
    [InlineData("123546789", null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("AAAAAA-AAAA-9", null)]
    [InlineData("690755003842/8", null)]
    [InlineData("690755003842/AB", null)]
    public void Rang_extrait_du_suffixe_sans_erreur_pour_les_formats_atypiques(string? numAssure, string? attendu)
    {
        Assert.Equal(attendu, RangAssure.Extraire(numAssure));
        Assert.Equal(attendu is null ? "" : $"rang {attendu}", RangAssure.Libelle(numAssure));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("2026-11-14", true)]
    [InlineData("2026-11-15", false)]   // droits valables jusqu'au jour de fin inclus
    [InlineData("2026-12-31", false)]
    public void Droits_expires_seulement_apres_la_date_de_fin(string? fin, bool attendu) =>
        Assert.Equal(attendu, DroitsChifa.Expires(fin is null ? null : DateOnly.Parse(fin), new DateOnly(2026, 11, 15)));

    private static (Encaisser Cas, VentesEspion Ventes, JournalEspion Journal) CasCaisse(DateOnly? finDroit)
    {
        var volets = new VoletsChifaEnMemoire();
        var ventes = new VentesEspion();
        var journal = new JournalEspion();
        var patients = new PatientsEnMemoire
        {
            Fiche = new FichePatient(12, "Benali", "Karim", null, null, null, "690755008888/02", 1, false, 0, DateFinDroit: finDroit)
        };
        var cas = new Encaisser(new CatalogueEnMemoire(), patients, ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(), journal: journal, voletsChifa: volets);
        return (cas, ventes, journal);
    }

    private static CommandeVente TiersPayantCnas() =>
        new([new LigneCommande(2, 20, 1)], true, 1, 80, 12, new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null), 0,
            ModePaiement.Especes, 100_000m, MotifSansVoletCode: "carte_absente");

    [Fact]
    public async Task Vente_CNAS_droits_expires_enregistree_et_avertissement_journalise()
    {
        var fin = CatalogueEnMemoire.Aujourdhui.AddDays(-1);
        var (cas, ventes, journal) = CasCaisse(fin);
        await cas.ExecuterAsync(TiersPayantCnas(), 5, "Caisse 1");
        Assert.NotNull(ventes.Recu);   // pas de blocage
        var i = journal.Actions.IndexOf(DroitsChifa.ActionJournal);
        Assert.True(i >= 0);
        Assert.Equal(fin, journal.Details[i]["date_fin_droit"]);
    }

    [Fact]
    public async Task Vente_CNAS_droits_valables_aucun_avertissement()
    {
        var (cas, ventes, journal) = CasCaisse(CatalogueEnMemoire.Aujourdhui);
        await cas.ExecuterAsync(TiersPayantCnas(), 5, "Caisse 1");
        Assert.NotNull(ventes.Recu);
        Assert.DoesNotContain(DroitsChifa.ActionJournal, journal.Actions);
    }

    [Fact]
    public async Task Sans_tiers_payant_Chifa_rien_n_est_journalise()
    {
        var journal = new JournalEspion();
        var patients = new PatientsEnMemoire { Fiche = new FichePatient(12, "Benali", null, null, null, null, null, 1, false, 0, DateFinDroit: new DateOnly(2020, 1, 1)) };
        await DroitsChifa.JournaliserSiExpiresAsync(patients, journal, 12, false, new DateOnly(2026, 11, 15), 5, 1, "V1", CancellationToken.None);
        Assert.Empty(journal.Actions);
        await DroitsChifa.JournaliserSiExpiresAsync(patients, journal, 12, true, new DateOnly(2026, 11, 15), 5, 1, "V1", CancellationToken.None);
        Assert.Equal([DroitsChifa.ActionJournal], journal.Actions);
    }
}
