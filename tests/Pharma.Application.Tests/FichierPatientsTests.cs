using Pharma.Application.Commun;
using Pharma.Application.Patients;
using Pharma.Domain.Securite;

namespace Pharma.Application.Tests;

/// <summary>Patients › « Fichier des patients » (2026-11-18) : droits, filtres transmis au dépôt, pagination, dette et état des droits Chifa.</summary>
public class FichierPatientsTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 18);

    private sealed class FichierEspion(params LigneFichierPatient[] lignes) : IFichierPatients
    {
        public FiltreFichierPatients? Filtre { get; private set; }
        public DemandePage? Page { get; private set; }

        public Task<Page<LigneFichierPatient>> PageAsync(FiltreFichierPatients filtre, DemandePage page, CancellationToken ct = default)
        {
            (Filtre, Page) = (filtre, page);
            return Task.FromResult(new Page<LigneFichierPatient>(lignes.Skip(page.Decalage).Take(page.Taille).ToList(), lignes.Length, page.Page, page.Taille));
        }
    }

    private static LigneFichierPatient Ligne(long id, string? organisme = "CNAS", DateOnly? fin = null, decimal dette = 0) =>
        new(id, $"Patient {id}", "Fictif", "0550000000", $"0000000000{id:00}", organisme, fin, false, false, dette);

    private static (ListerFichierPatients Cas, FichierEspion Depot) Creer(string[] droits, params LigneFichierPatient[] lignes)
    {
        var depot = new FichierEspion(lignes);
        var sessions = new SessionsEnMemoire { Parametres = new(true, 5000, 500, 0) { JoursAlerteFinDroit = 30 } };
        return (new ListerFichierPatients(depot, sessions, new HorlogeFixe(Aujourdhui), new DroitsFactices(droits)), depot);
    }

    [Fact]
    public async Task Sans_le_droit_de_vendre_le_fichier_est_refuse()
    {
        var (cas, depot) = Creer([Permissions.StockConsulter], Ligne(1));

        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync(new SaisieFiltreFichierPatients(), 7, new DemandePage()));
        Assert.Null(depot.Filtre);
    }

    [Fact]
    public void Memes_droits_que_la_fiche_patient_et_que_la_dette()
    {
        Assert.Equal(Permissions.CaisseVendre, ListerFichierPatients.Droit);
        Assert.Equal(Permissions.CaisseVendre, ListerFichierPatients.DroitCredit);
    }

    [Fact]
    public async Task Les_filtres_sont_transmis_controles()
    {
        var (cas, depot) = Creer([Permissions.CaisseVendre]);

        await cas.ExecuterAsync(new SaisieFiltreFichierPatients("  Ben Ali  ", "3", true, true, "expires", false), 7, new DemandePage(2, 20));

        var f = depot.Filtre!;
        Assert.Equal("Ben Ali", f.Texte);
        Assert.Equal(3, f.OrganismeId);
        Assert.False(f.SansOrganisme);
        Assert.True(f.Chronique);
        Assert.True(f.ACredit);
        Assert.Equal("expires", f.Droits);
        Assert.False(f.AccordSms);
        Assert.Equal(Aujourdhui, f.Aujourdhui);
        Assert.Equal(30, f.JoursAlerte);
        Assert.Equal(new DemandePage(2, 20), depot.Page);
    }

    [Theory]
    [InlineData("aucun", null, true)]
    [InlineData("12", 12L, false)]
    [InlineData("0", null, false)]
    [InlineData("CNAS", null, false)]
    [InlineData(null, null, false)]
    public async Task Organisme_id_ou_aucun_sinon_ignore(string? saisi, long? id, bool sans)
    {
        var (cas, depot) = Creer([Permissions.CaisseVendre]);

        await cas.ExecuterAsync(new SaisieFiltreFichierPatients(Organisme: saisi), 7, new DemandePage());

        Assert.Equal(id, depot.Filtre!.OrganismeId);
        Assert.Equal(sans, depot.Filtre.SansOrganisme);
    }

    [Theory]
    [InlineData("expires_ou_bientot", "expires_ou_bientot")]
    [InlineData("bientot", "bientot")]
    [InlineData("tous", null)]
    [InlineData("'; DROP TABLE patient; --", null)]
    public async Task Etat_des_droits_borne(string saisi, string? attendu)
    {
        var (cas, depot) = Creer([Permissions.CaisseVendre]);

        await cas.ExecuterAsync(new SaisieFiltreFichierPatients(Droits: saisi), 7, new DemandePage());

        Assert.Equal(attendu, depot.Filtre!.Droits);
    }

    [Fact]
    public async Task Texte_vide_ignore_et_texte_long_tronque()
    {
        var (cas, depot) = Creer([Permissions.CaisseVendre]);

        await cas.ExecuterAsync(new SaisieFiltreFichierPatients("   "), 7, new DemandePage());
        Assert.Null(depot.Filtre!.Texte);

        await cas.ExecuterAsync(new SaisieFiltreFichierPatients(new string('a', 300)), 7, new DemandePage());
        Assert.Equal(ListerFichierPatients.LongueurTexteMax, depot.Filtre!.Texte!.Length);
    }

    [Fact]
    public async Task Pagination_du_depot_rendue_telle_quelle_avec_le_total()
    {
        var lignes = Enumerable.Range(1, 23).Select(i => Ligne(i)).ToArray();
        var (cas, _) = Creer([Permissions.CaisseVendre], lignes);

        var page = await cas.ExecuterAsync(new SaisieFiltreFichierPatients(), 7, DemandePage.Lire(3, 10));

        Assert.Equal(23, page.Total);
        Assert.Equal(3, page.Numero);
        Assert.Equal([21L, 22L, 23L], page.Lignes.Select(l => l.Id));
    }

    [Fact]
    public async Task Dette_montree_avec_le_droit_qui_la_montre_deja()
    {
        var (cas, _) = Creer([Permissions.CaisseVendre], Ligne(1, dette: 1250.5m));

        var page = await cas.ExecuterAsync(new SaisieFiltreFichierPatients(), 7, new DemandePage());

        Assert.Equal(1250.5m, page.Lignes[0].SoldeCredit);
    }

    [Fact]
    public async Task Etat_des_droits_chifa_calcule_pour_cnas_et_casnos_seulement()
    {
        var (cas, _) = Creer([Permissions.CaisseVendre],
            Ligne(1, "CNAS", Aujourdhui.AddDays(-1)), Ligne(2, "CASNOS", Aujourdhui.AddDays(30)), Ligne(3, "CNAS", Aujourdhui.AddDays(31)),
            Ligne(4, "MDN", Aujourdhui.AddDays(-5)), Ligne(5, null, null), Ligne(6, "CNAS", Aujourdhui));

        var page = await cas.ExecuterAsync(new SaisieFiltreFichierPatients(), 7, new DemandePage());

        Assert.Equal(["expires", "bientot", null, null, null, "bientot"], page.Lignes.Select(l => l.EtatDroits));
    }
}
