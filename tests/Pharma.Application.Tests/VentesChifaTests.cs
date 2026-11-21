using Pharma.Application.Chifa;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Referentiels;
using Pharma.Domain.Securite;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

internal sealed class VentesChifaEnMemoire : IVentesChifa
{
    public List<VenteChifa> Ventes { get; } = [];
    public InfosChifa? DernieresInfos { get; private set; }
    public bool CentreRetenu { get; private set; }

    public Task<VenteChifa?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Ventes.FirstOrDefault(v => v.Id == id));
    public Task<Page<VenteChifa>> ListerAsync(FiltreVentesChifa f, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(PageEnMemoire.Paginer(Ventes.Where(v => f.Exclues == FiltreVentesChifa.Tous || (f.Exclues == FiltreVentesChifa.Oui) == (v.ExclueLe is not null)).ToList(), page));
    public Task<bool> EnregistrerInfosAsync(long id, InfosChifa infos, bool retenir, CancellationToken ct = default)
    {
        (DernieresInfos, CentreRetenu) = (infos, retenir);
        return Task.FromResult(true);
    }
    public Task<bool> ExclureAsync(long id, string code, string? texte, long u, CancellationToken ct = default)
    {
        var i = Ventes.FindIndex(v => v.Id == id);
        Ventes[i] = Ventes[i] with { ExclueLe = DateTime.UtcNow, ExclusionCode = code, Exclusion = texte ?? code };
        return Task.FromResult(true);
    }
    public Task<bool> RetablirAsync(long id, CancellationToken ct = default)
    {
        var i = Ventes.FindIndex(v => v.Id == id);
        Ventes[i] = Ventes[i] with { ExclueLe = null, ExclusionCode = null, Exclusion = null };
        return Task.FromResult(true);
    }
    public Task<string?> EmployeActifAsync(long id, CancellationToken ct = default) => Task.FromResult<string?>(id == 5 ? "Opératrice" : null);
    public Task<IReadOnlyList<(long Id, string Nom)>> EmployesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<(long, string)>>([(1, "Vendeur"), (5, "Opératrice")]);

    // Retours à corriger dans CHIFA-Officine (script 155)
    public List<RetourACorrigerChifa> Retours { get; } = [];
    public FiltreRetoursACorriger? DernierFiltre { get; private set; }
    public Task<Page<RetourACorrigerChifa>> RetoursACorrigerAsync(FiltreRetoursACorriger f, DemandePage page, CancellationToken ct = default)
    {
        DernierFiltre = f;
        return Task.FromResult(PageEnMemoire.Paginer(Retours.Where(r => f.Etat == FiltreRetoursACorriger.Tous
            || (f.Etat == FiltreRetoursACorriger.Corriges) == (r.CorrigeLe is not null)).ToList(), page));
    }
    public Task<RetourACorrigerChifa?> MarquerRetourCorrigeAsync(long id, long u, CancellationToken ct = default)
    {
        var i = Retours.FindIndex(r => r.RetourId == id && r.CorrigeLe is null);
        if (i < 0) return Task.FromResult<RetourACorrigerChifa?>(null);
        Retours[i] = Retours[i] with { CorrigeLe = DateTime.UtcNow, CorrigePar = "Opératrice" };
        return Task.FromResult<RetourACorrigerChifa?>(Retours[i]);
    }
}

internal sealed class CentresEnMemoire : ICentresPayeurs
{
    public List<CentrePayeur> Centres { get; } = [new(3, 1, "CNAS", "1600", "Centre A", true), new(4, 2, "CASNOS", "16", "Centre B", true), new(5, 1, "CNAS", "1601", "Ancien", false)];
    public Task<Page<CentrePayeur>> ListerAsync(long? o, bool? a, string? q, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult(PageEnMemoire.Paginer(Centres.Where(c => o is null || c.OrganismeId == o).ToList(), page));
    public Task<CentrePayeur?> LireAsync(long id, CancellationToken ct = default) => Task.FromResult(Centres.FirstOrDefault(c => c.Id == id));
    public Task<string?> CodeOrganismeAsync(long id, CancellationToken ct = default) => Task.FromResult<string?>(id switch { 1 => "CNAS", 2 => "CASNOS", 9 => "MDN", _ => null });
    public Task<long> EnregistrerAsync(long? id, long o, string code, string nom, bool actif, CancellationToken ct = default)
    {
        var n = id ?? Centres.Max(c => c.Id) + 1;
        Centres.RemoveAll(c => c.Id == n);
        Centres.Add(new CentrePayeur(n, o, o == 1 ? "CNAS" : "CASNOS", code, nom, actif));
        return Task.FromResult(n);
    }
}

/// <summary>Volet Chifa : compléments (2026-11-15, script 100) : informations, majoration, exclusion, centres payeurs.</summary>
public class VentesChifaTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);

    private static VenteChifa Vente(long id = 10, string canal = "chifa", string statut = "validee", bool bordereau = false) =>
        new(id, "T-000010", new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc), canal, statut, bordereau, false, 7, "Patient", "123456789012/01",
            1, "CNAS", "Vendeur", 1, 1000m, 800m, null, new InfosChifa(null, null, null, null, 0), null, null, null);

    private static MotifsEnMemoire Motifs()
    {
        var m = new MotifsEnMemoire();
        m.Motifs.Add(new MotifReglable(50, "majoration_chifa", "garde", "Garde", true, false, 10));
        m.Motifs.Add(new MotifReglable(51, "chifa_exclusion", "erreur_saisie", "Erreur de saisie", true, false, 30));
        m.Motifs.Add(new MotifReglable(52, "chifa_exclusion", "autre", "Autre (préciser)", true, true, 999));
        return m;
    }

    [Fact]
    public void Majoration_s_ajoute_a_la_part_organisme()
    {
        Assert.Equal(850m, RegleInfosChifa.PartOrganismeTotale(800m, 50m));
        Assert.Equal(800m, RegleInfosChifa.PartOrganismeTotale(800m, 0m));
    }

    [Fact]
    public void Majoration_avec_montant_exige_un_type_et_date_des_soins_pas_dans_le_futur()
    {
        Assert.Throws<RegleMetierException>(() => RegleInfosChifa.Verifier(new InfosChifa(null, null, null, null, 50m), Aujourdhui));
        Assert.Throws<RegleMetierException>(() => RegleInfosChifa.Verifier(new InfosChifa(null, null, null, "garde", -1m), Aujourdhui));
        Assert.Throws<RegleMetierException>(() => RegleInfosChifa.Verifier(new InfosChifa(Aujourdhui.AddDays(1), null, null, null, 0), Aujourdhui));
        Assert.Null(RegleInfosChifa.Verifier(new InfosChifa(Aujourdhui, null, null, "garde", 0), Aujourdhui).MajorationCode);
    }

    [Fact]
    public async Task Infos_enregistrees_avec_saisi_par_par_defaut_le_vendeur_et_journalisees()
    {
        var ventes = new VentesChifaEnMemoire();
        ventes.Ventes.Add(Vente());
        var journal = new JournalEspion();
        var cas = new EnregistrerInfosChifa(ventes, new CentresEnMemoire(), new DroitsFactices(Permissions.CaisseVendre), journal, new HorlogeFixe(Aujourdhui), Motifs());

        await cas.ExecuterAsync(10, new InfosChifa(Aujourdhui, 3, null, "garde", 120m), true, 1);

        Assert.Equal(1, ventes.DernieresInfos!.SaisiPar);
        Assert.Equal(120m, ventes.DernieresInfos.MajorationMontant);
        Assert.True(ventes.CentreRetenu);
        Assert.Equal(EnregistrerInfosChifa.Action, Assert.Single(journal.Actions));
    }

    [Fact]
    public async Task Centre_d_un_autre_organisme_ou_desactive_refuse_et_vente_dans_un_bordereau_figee()
    {
        var ventes = new VentesChifaEnMemoire();
        ventes.Ventes.Add(Vente());
        ventes.Ventes.Add(Vente(11, bordereau: true));
        var cas = new EnregistrerInfosChifa(ventes, new CentresEnMemoire(), new DroitsFactices(Permissions.CaisseVendre), new JournalEspion(), new HorlogeFixe(Aujourdhui), Motifs());

        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(10, new InfosChifa(null, 4, null, null, 0), false, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(10, new InfosChifa(null, 5, null, null, 0), false, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(10, new InfosChifa(null, null, 99, null, 0), false, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(11, new InfosChifa(null, null, null, null, 0), false, 1));
    }

    [Fact]
    public async Task Retours_a_corriger_dans_CHIFA_Officine_filtres_puis_marques_corriges_une_seule_fois_et_journalises()
    {
        var ventes = new VentesChifaEnMemoire();
        ventes.Retours.Add(new RetourACorrigerChifa(40, DateTime.UtcNow, "Vendeur", 10, "T-000010", DateTime.UtcNow, "Patient", null, "06931126",
            "1 × Sirop fictif", 400m, false, null, null));
        var journal = new JournalEspion();
        var consulter = new ConsulterVentesChifa(ventes, new DroitsFactices(Permissions.CaisseVendre));
        var marquer = new EnregistrerInfosChifa(ventes, new CentresEnMemoire(), new DroitsFactices(Permissions.CaisseVendre), journal, new HorlogeFixe(Aujourdhui), Motifs());

        // État inconnu : « à corriger » par défaut
        var p = await consulter.RetoursACorrigerAsync(new FiltreRetoursACorriger("  T-0000  ", "n'importe"), new DemandePage(1, 20), 1);
        Assert.Equal((FiltreRetoursACorriger.ACorriger, "T-0000"), (ventes.DernierFiltre!.Etat, ventes.DernierFiltre.Recherche));
        Assert.Single(p.Lignes);

        await marquer.MarquerRetourCorrigeAsync(40, 1);
        Assert.Equal(EnregistrerInfosChifa.ActionRetourCorrige, Assert.Single(journal.Actions));
        Assert.Empty((await consulter.RetoursACorrigerAsync(new FiltreRetoursACorriger(null), new DemandePage(1, 20), 1)).Lignes);
        Assert.Single((await consulter.RetoursACorrigerAsync(new FiltreRetoursACorriger(null, FiltreRetoursACorriger.Corriges), new DemandePage(1, 20), 1)).Lignes);
        // Déjà marqué : refusé
        await Assert.ThrowsAsync<RegleMetierException>(() => marquer.MarquerRetourCorrigeAsync(40, 1));
        // Sans le droit de vendre : refusé
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new ConsulterVentesChifa(ventes, new DroitsFactices()).RetoursACorrigerAsync(new FiltreRetoursACorriger(null), new DemandePage(1, 20), 1));
    }

    [Fact]
    public async Task Exclusion_sans_le_droit_est_refusee()
    {
        var ventes = new VentesChifaEnMemoire();
        ventes.Ventes.Add(Vente());
        var cas = new ExclureVenteChifa(ventes, new DroitsFactices(Permissions.CaisseVendre), new JournalEspion(), Motifs());
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExclureAsync(10, "erreur_saisie", null, 1));
        Assert.Null(ventes.Ventes[0].ExclueLe);
    }

    [Fact]
    public async Task Exclusion_puis_retablissement_journalises_autre_exige_un_texte()
    {
        var ventes = new VentesChifaEnMemoire();
        ventes.Ventes.Add(Vente());
        var journal = new JournalEspion();
        var cas = new ExclureVenteChifa(ventes, new DroitsFactices(Permissions.ChifaExclure), journal, Motifs());

        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExclureAsync(10, "autre", null, 1));
        await cas.ExclureAsync(10, "erreur_saisie", null, 1);
        Assert.NotNull(ventes.Ventes[0].ExclueLe);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExclureAsync(10, "erreur_saisie", null, 1));
        await cas.RetablirAsync(10, 1);
        Assert.Null(ventes.Ventes[0].ExclueLe);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RetablirAsync(10, 1));
        Assert.Equal([ExclureVenteChifa.ActionExclure, ExclureVenteChifa.ActionRetablir], journal.Actions);
    }

    [Fact]
    public void Exclusion_refusee_hors_chifa_annulee_ou_en_bordereau()
    {
        Assert.Throws<RegleMetierException>(() => RegleExclusionChifa.VerifierExclusion("logiciel", "validee", false, false));
        Assert.Throws<RegleMetierException>(() => RegleExclusionChifa.VerifierExclusion("chifa", "annulee", false, false));
        Assert.Throws<RegleMetierException>(() => RegleExclusionChifa.VerifierExclusion("chifa", "validee", false, true));
    }

    [Fact]
    public void Le_droit_chifa_exclure_n_est_pas_autorisable_par_code_comptoir()
    {
        Assert.Equal("chifa.exclure", Permissions.ChifaExclure);
        Assert.False(CodeComptoir.AutorisableParCollegue(Permissions.ChifaExclure));
    }

    [Fact]
    public async Task Centre_payeur_reserve_a_cnas_casnos_code_de_1_a_10_caracteres()
    {
        var centres = new CentresEnMemoire();
        var cas = new CentresPayeurs(centres, new DroitsFactices(Permissions.AdminParametres), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.EnregistrerAsync(null, 9, "1", "Militaire", true, 1));
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.EnregistrerAsync(null, 1, "12345678901", "Trop long", true, 1));
        var c = await cas.EnregistrerAsync(null, 2, " C-07 ", "Centre  C", true, 1);
        Assert.Equal(("C-07", "Centre C"), (c.Code, c.Nom));
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new CentresPayeurs(centres, new DroitsFactices(Permissions.CaisseVendre), new JournalEspion()).EnregistrerAsync(null, 1, "X", "Y", true, 1));
    }
}
