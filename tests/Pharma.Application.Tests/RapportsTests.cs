using System.Text;
using Pharma.Application.Commun;
using Pharma.Application.Rapports;
using Pharma.Domain.Rapports;

namespace Pharma.Application.Tests;

internal sealed class RapportsEnMemoire : IRapports
{
    public (DateOnly Du, DateOnly Au)? Demande { get; private set; }
    public Task<SyntheseVentes> SyntheseAsync(DateOnly du, DateOnly au, CancellationToken ct = default)
    {
        Demande = (du, au);
        return Task.FromResult(new SyntheseVentes(12, 15400.50m, 3200, 800, 150, 1));
    }
    public Task<IReadOnlyList<VentesJour>> ParJourAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VentesJour>>([new(du, 12, 15400.50m)]);
    public Task<IReadOnlyList<ProduitVendu>> TopProduitsAsync(DateOnly du, DateOnly au, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitVendu>>([new("=HYPERLINK(\"x\")", 3, 840), new("Paracétamol 500 mg cp", 20, 1200)]);
    public Task<MargeBrute> MargeAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult(new MargeBrute(15000, 11000));
    public Task<IReadOnlyList<ClotureRapport>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClotureRapport>>([new("Caissier", "Poste 1", DateTime.UtcNow.AddHours(-8), DateTime.UtcNow, 42300, 41800, -500, 1, 0)]);
}

public class RapportsTests
{
    [Fact]
    public async Task Sans_droits_marge_et_ecarts_masques()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 2);
        Assert.Null(r.Marge);
        Assert.False(r.EcartsVisibles);
        Assert.All(r.Clotures, c => { Assert.Null(c.Attendu); Assert.Null(c.Ecart); Assert.NotNull(c.Compte); });
    }

    [Fact]
    public async Task Avec_droits_marge_et_ecarts_visibles_sur_la_semaine()
    {
        var depot = new RapportsEnMemoire();
        var r = await new ObtenirRapport(depot, new DroitsFactices("rapports.ventes", "rapports.marges", "caisse.voir_ecart"))
            .ExecuterAsync(TypePeriode.Semaine, new DateOnly(2026, 11, 12), 1);
        Assert.Equal((new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 15)), depot.Demande);
        Assert.Equal((4000m, 26.7m), (r.Marge!.Montant, r.Marge.Taux!.Value));
        Assert.Equal(-500, r.Clotures[0].Ecart);
    }

    [Fact]
    public async Task Rapports_refuses_sans_droit() =>
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("caisse.vendre")).ExecuterAsync(TypePeriode.Mois, new DateOnly(2026, 11, 1), 3));

    [Fact]
    public async Task Csv_pour_excel_francais_sans_formule_executable()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes", "rapports.marges")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 1);
        var octets = RapportCsv.Ecrire(r);
        Assert.Equal(Encoding.UTF8.GetPreamble(), octets[..3]);                // BOM : accents corrects dans Excel
        var texte = Encoding.UTF8.GetString(octets[3..]);
        Assert.Contains("Chiffre d'affaires TTC;15400,50", texte);              // virgule décimale, séparateur ;
        Assert.Contains("Marge brute;4000,00", texte);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\";3;840,00", texte);            // formule neutralisée
        Assert.DoesNotContain(";-500", texte);                                  // écart non visible sans le droit
    }
}

public class RapportParSemaineTests
{
    [Fact]
    public void Ventes_regroupees_par_semaine_sur_une_longue_periode()
    {
        var p = Pharma.Domain.Rapports.PeriodeRapport.Choisie(new(2026, 6, 3), new(2026, 11, 9), new(2026, 11, 9));
        var jours = new List<Pharma.Application.Rapports.VentesJour>
        {
            new(new(2026, 6, 3), 1, 100), new(new(2026, 6, 5), 2, 200),   // semaine du lundi 1er juin, coupée au 3
            new(new(2026, 6, 8), 1, 50)                                    // semaine du lundi 8 juin
        };
        var s = Pharma.Application.Rapports.ObtenirRapport.ParSemaine(p, jours);
        Assert.Equal(2, s.Count);
        Assert.Equal((new DateOnly(2026, 6, 3), 3, 300m), (s[0].Date, s[0].NombreVentes, s[0].ChiffreAffaires));
        Assert.Equal(new DateOnly(2026, 6, 8), s[1].Date);
    }
}
