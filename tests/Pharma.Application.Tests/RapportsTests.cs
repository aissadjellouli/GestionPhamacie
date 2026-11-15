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
    public (DateOnly Jour, TimeSpan Heure)? DemandeHeure { get; private set; }
    public Task<SyntheseVentes> SyntheseJusquAAsync(DateOnly jour, TimeSpan heure, CancellationToken ct = default)
    {
        DemandeHeure = (jour, heure);
        return Task.FromResult(new SyntheseVentes(10, 14000m, 3000, 700, 100, 0));
    }
    public Task<IReadOnlyList<VentesJour>> ParJourAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<VentesJour>>([new(du, 12, 15400.50m)]);
    public Task<IReadOnlyList<ProduitVendu>> TopProduitsAsync(DateOnly du, DateOnly au, int limite, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProduitVendu>>([new("=HYPERLINK(\"x\")", 3, 840), new("Paracétamol 500 mg cp", 20, 1200)]);
    public Task<MargeBrute> MargeAsync(DateOnly du, DateOnly au, CancellationToken ct = default) => Task.FromResult(new MargeBrute(15000, 11000));
    public Task<IReadOnlyList<ClotureRapport>> CloturesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ClotureRapport>>([new("Caissier", "Poste 1", DateTime.UtcNow.AddHours(-8), DateTime.UtcNow, 42300, 41800, -500, 1, 0)]);
    public Task<IReadOnlyList<RetourRapport>> RetoursAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RetourRapport>>([new("T-000020", new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc), "Vendeur", new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc),
            "Caisse 2", "Assistante", "Sirop fictif × 1", 500, 300, false)]);
    public Task<IReadOnlyList<SortieSansVente>> SortiesAsync(DateOnly du, DateOnly au, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SortieSansVente>>([
            new(new DateTime(2026, 11, 9, 11, 0, 0, DateTimeKind.Utc), "echantillon", "Crème fictive", "L1", 2, 300, "Vendeur", "=Dr X, échantillon", null, 57),
            new(new DateTime(2026, 11, 9, 10, 0, 0, DateTimeKind.Utc), "perime", "Sirop fictif", "L2", 3, 450.5m, "Assistante", null, null, 0),
            new(new DateTime(2026, 11, 9, 9, 0, 0, DateTimeKind.Utc), "casse", "Crème fictive", "L1", 1, 150, "Vendeur", "flacon tombé", null, 28.5m)]);
    public TvaSorties Tva { get; set; } = new(false, false, false);
    public Task<TvaSorties> TvaSortiesAsync(CancellationToken ct = default) => Task.FromResult(Tva);
    public Task ModifierTvaSortiesAsync(TvaSorties tva, CancellationToken ct = default) { Tva = tva; return Task.CompletedTask; }
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
        Assert.Contains("Chiffre d'affaires TTC (retours déduits);15400,50", texte);            // virgule décimale, séparateur ;
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

public class SortiesRapportTests
{
    [Fact]
    public async Task Valeur_des_pertes_et_dons_masquee_sans_droit_marges()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 2);
        Assert.Equal(3, r.Sorties!.Count);
        Assert.All(r.Sorties, s => Assert.Null(s.Valeur));
        Assert.Null(SortiesRapport.Total("Total", r.Sorties).Valeur);
    }

    [Fact]
    public async Task Totaux_par_motif_employe_et_produit()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes", "rapports.marges")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 1);
        var total = SortiesRapport.Total("Total", r.Sorties!);
        Assert.Equal((3, 1, 2, 6, 900.5m), (total.Perimes, total.Casses, total.Dons, total.Boites, total.Valeur!.Value));
        var parEmploye = SortiesRapport.ParEmploye(r.Sorties!);
        Assert.Equal(["Assistante", "Vendeur"], parEmploye.Select(t => t.Cle));      // plus forte valeur d'abord (450,50 contre 450)
        Assert.Equal((0, 1, 2, 450m), (parEmploye[1].Perimes, parEmploye[1].Casses, parEmploye[1].Dons, parEmploye[1].Valeur!.Value));
        var parProduit = SortiesRapport.ParProduit(r.Sorties!);
        Assert.Equal((3, 450m), (parProduit.Single(t => t.Cle == "Crème fictive").Boites, parProduit.Single(t => t.Cle == "Crème fictive").Valeur!.Value));
    }

    [Fact]
    public async Task Csv_contient_les_sorties_sans_formule_executable()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes", "rapports.marges")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 1);
        var texte = Encoding.UTF8.GetString(RapportCsv.Ecrire(r));
        Assert.Contains("Sorties de stock sans vente;", texte);
        Assert.Contains("Total;3;1;2;900,50", texte);
        Assert.Contains("Don ou échantillon;Crème fictive;L1;2;300,00;57,00;non;Vendeur;'=Dr X, échantillon", texte);
    }
}

public class MargeApresSortiesTests
{
    [Fact]
    public async Task Sans_tva_reglee_seule_la_valeur_des_pertes_et_dons_est_retiree()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes", "rapports.marges")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 1);
        // Marge brute 4 000 - pertes et dons 900,50 - TVA 0
        Assert.Equal((4000m, 900.5m, 0m, 3099.5m), (r.ApresSorties!.MargeBrute, r.ApresSorties.PertesHt, r.ApresSorties.TvaAReverser, r.ApresSorties.Montant));
    }

    [Fact]
    public async Task Tva_retiree_seulement_pour_les_motifs_regles()
    {
        var depot = new RapportsEnMemoire { Tva = new TvaSorties(Perime: false, Casse: true, Don: true) };
        var r = await new ObtenirRapport(depot, new DroitsFactices("rapports.ventes", "rapports.marges")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 1);
        Assert.Equal((85.5m, 3014m), (r.ApresSorties!.TvaAReverser, r.ApresSorties.Montant));   // don 57 + casse 28,50
    }

    [Fact]
    public async Task Sans_droit_marges_ni_marge_apres_sorties_ni_tva()
    {
        var r = await new ObtenirRapport(new RapportsEnMemoire(), new DroitsFactices("rapports.ventes")).ExecuterAsync(TypePeriode.Jour, new DateOnly(2026, 11, 9), 2);
        Assert.Null(r.ApresSorties);
        Assert.All(r.Sorties!, s => Assert.Null(s.TvaAchat));
    }

    [Fact]
    public async Task Reglage_de_la_tva_reserve_au_titulaire_et_journalise()
    {
        var depot = new RapportsEnMemoire();
        var journal = new JournalEspion();
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ModifierTvaSorties(depot, new DroitsFactices("rapports.ventes", "rapports.marges"), journal).ExecuterAsync(new TvaSorties(true, true, true), 2));
        Assert.Equal(new TvaSorties(false, false, false), depot.Tva);

        await new ModifierTvaSorties(depot, new DroitsFactices() { Titulaire = true }, journal).ExecuterAsync(new TvaSorties(false, false, true), 1);
        Assert.Equal(new TvaSorties(false, false, true), depot.Tva);
        Assert.Equal(["tva_sorties"], journal.Actions);
    }
}
