using Pharma.Application.Caisse;
using Pharma.Application.Commun;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Ventes Chifa en attente de volet, en mémoire : rattachement enregistré (une seule fois par vente et par volet).</summary>
internal sealed class RapprochementsEnMemoire : IRapprochementsVolet
{
    public List<VenteAttenteVolet> Ventes { get; } = [];
    public List<RattachementVolet> Rattaches { get; } = [];
    public DateTime? Depuis { get; private set; }

    public Task<IReadOnlyList<VenteAttenteVolet>> CandidatesAsync(DateTime depuisUtc, int max, CancellationToken ct = default)
    {
        Depuis = depuisUtc;
        return Task.FromResult<IReadOnlyList<VenteAttenteVolet>>(Ventes.Where(v => v.Date >= depuisUtc && Rattaches.All(r => r.VenteId != v.Id)).Take(max).ToList());
    }

    public Task<VenteAttenteVolet?> VenteAsync(long venteId, CancellationToken ct = default) =>
        Task.FromResult(Ventes.FirstOrDefault(v => v.Id == venteId && Rattaches.All(r => r.VenteId != venteId)));

    public Task<bool> RattacherAsync(RattachementVolet r, CancellationToken ct = default)
    {
        if (Rattaches.Any(x => x.Qr.NumeroFacture == r.Qr.NumeroFacture))
            throw new RegleMetierException($"Le volet Chifa n° {r.Qr.NumeroFacture} vient d’être rattaché à une autre vente.", "volet_chifa");
        if (Rattaches.Any(x => x.VenteId == r.VenteId)) return Task.FromResult(false);
        Rattaches.Add(r);
        return Task.FromResult(true);
    }

    public Task<(IReadOnlyList<VenteAttenteVolet> Lignes, int Total)> EnAttenteAsync(string? recherche, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<VenteAttenteVolet>, int)>((Ventes, Ventes.Count));

    public Task<(IReadOnlyList<VoletAvecEcart> Lignes, int Total)> AvecEcartAsync(string? recherche, DemandePage page, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<VoletAvecEcart>, int)>(([], 0));
}

/// <summary>Circuit « la caisse d'abord » (règle du titulaire, 2026-11-15) : volet rapproché d'une vente déjà encaissée.</summary>
public class RapprochementVoletTests
{
    // 3 boîtes de VIT D3 à 156,48 DA ; assuré 690755003842/80
    private const string Volet = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;";
    private static readonly DateTime Maintenant = CatalogueEnMemoire.Aujourdhui.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc);

    private static VenteAttenteVolet Vente(long id, string numAssure, int quantite, decimal prix = 156.48m, int joursAvant = 0, long produit = 18,
        decimal part = 375.55m, decimal? taux = 80) =>
        new(id, $"T-{id:000000}", Maintenant.AddDays(-joursAvant).AddHours(-1), 12, "Benali Amine", numAssure, "CNAS", taux, prix * quantite, part, "Vendeur A",
            [new LigneVenteRapprochee(produit, produit == 18 ? "VIT D3" : "Autre produit", quantite, prix)]);

    private static (RapprocherVoletChifa Cas, RapprochementsEnMemoire Ventes, VoletsChifaEnMemoire Volets, JournalEspion Journal) Cas(params string[] droits)
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(18, "VIT D3", 160m);
        var ventes = new RapprochementsEnMemoire();
        var journal = new JournalEspion();
        var cas = new RapprocherVoletChifa(volets, ventes, new SessionsEnMemoire(), new DroitsFactices(droits.Length == 0 ? ["caisse.vendre"] : droits),
            journal, new HorlogeFixe(CatalogueEnMemoire.Aujourdhui));
        return (cas, ventes, volets, journal);
    }

    [Fact]
    public async Task Candidates_du_meme_assure_classees_par_proximite()
    {
        var (cas, ventes, _, _) = Cas();
        ventes.Ventes.AddRange([
            Vente(1, "690755003842/80", 2),                // même assuré, quantité différente
            Vente(2, "690755003842/80", 3),                // même assuré, concorde : la meilleure
            Vente(3, "111111111111/01", 3),                // autre assuré : écarté (une vente du même assuré existe)
            Vente(4, "690755003842/80", 3, joursAvant: 8)  // plus de 7 jours : en retard, proposée après les autres (décision 2026-11-15)
        ]);
        var p = await cas.ProposerAsync(Volet, 5);
        Assert.Equal([2L, 1L, 4L], p.Candidates.Select(c => c.Vente.Id));
        Assert.True(p.Candidates[0].MemeAssure && p.Candidates[0].Comparaison.Concorde);
        Assert.Equal(RapprochementVolet.Quantite, Assert.Single(p.Candidates[1].Comparaison.Lignes).Ecart);
        Assert.Equal([false, false, true], p.Candidates.Select(c => c.EnRetard));
        Assert.Equal(RapprocherVoletChifa.SansLimite, ventes.Depuis);   // aucune expiration
        Assert.Equal(RapprocherVoletChifa.DelaiParDefaut, p.DelaiJours);
    }

    [Fact]
    public async Task Vente_en_retard_toujours_rapprochable_a_la_main()
    {
        var (cas, ventes, _, _) = Cas();
        ventes.Ventes.Add(Vente(4, "690755003842/80", 3, joursAvant: 40));
        Assert.True(Assert.Single((await cas.ProposerAsync(Volet, 5)).Candidates).EnRetard);
        var r = await cas.RattacherAsync(Volet, 4, null, 5);
        Assert.Equal("T-000004", r.Vente);
        Assert.Single(ventes.Rattaches);
    }

    [Theory]
    [InlineData(3, new long[] { 1, 2 }, new[] { false, true })]    // délai 3 jours : la vente d'il y a 5 jours est en retard
    [InlineData(10, new long[] { 1, 2 }, new[] { false, false })]  // délai 10 jours : les deux dans le délai (la plus récente d'abord)
    public async Task Delai_des_candidates_reglable(int delai, long[] ordre, bool[] enRetard)
    {
        var volets = new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(18, "VIT D3", 160m);
        var ventes = new RapprochementsEnMemoire();
        var sessions = new SessionsEnMemoire { Parametres = new ParametresCaisse(true, 5000, 500, 0, DelaiVoletChifaJours: delai) };
        var cas = new RapprocherVoletChifa(volets, ventes, sessions, new DroitsFactices("caisse.vendre"), new JournalEspion(), new HorlogeFixe(CatalogueEnMemoire.Aujourdhui));
        ventes.Ventes.AddRange([Vente(1, "690755003842/80", 3, joursAvant: 1), Vente(2, "690755003842/80", 3, joursAvant: 5)]);
        var p = await cas.ProposerAsync(Volet, 5);
        Assert.Equal(delai, p.DelaiJours);
        Assert.Equal(ordre, p.Candidates.Select(c => c.Vente.Id).ToArray());
        Assert.Equal(enRetard, p.Candidates.Select(c => c.EnRetard).ToArray());
    }

    [Fact]
    public async Task Delai_hors_bornes_refuse_dans_les_parametres()
    {
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new ModifierParametresCaisse(new SessionsEnMemoire(), new JournalEspion()).ExecuterAsync(new ParametresCaisse(true, 5000, 500, 0, DelaiVoletChifaJours: 0), 1));
        Assert.Equal("delai_volet_chifa", ex.Champ);
    }

    [Fact]
    public async Task Aucune_vente_du_meme_assure_celles_de_la_meme_carte_d_abord()
    {
        var (cas, ventes, _, _) = Cas();
        ventes.Ventes.AddRange([
            Vente(1, "111111111111/01", 3),              // autre carte, concorde : la plus proche
            Vente(2, "690755003842/01", 1),              // même carte (ayant droit), moins proche
            Vente(3, "222222222222/80", 3)
        ]);
        var p = await cas.ProposerAsync(Volet, 5);
        Assert.Equal(2L, p.Candidates[0].Vente.Id);
        Assert.True(p.Candidates[0].MemeCarte);
        Assert.Equal([2L, 1L, 3L], p.Candidates.Select(c => c.Vente.Id));
        Assert.All(p.Candidates.Skip(1), c => Assert.False(c.MemeCarte));
    }

    [Fact]
    public async Task Assure_sans_vente_toutes_les_candidates_classees_par_proximite()
    {
        var (cas, ventes, _, _) = Cas();
        ventes.Ventes.AddRange([Vente(1, "222222222222/00", 1, produit: 30), Vente(2, "111111111111/01", 3)]);
        var p = await cas.ProposerAsync(Volet, 5);
        Assert.Equal([2L, 1L], p.Candidates.Select(c => c.Vente.Id));
        Assert.All(p.Candidates, c => Assert.False(c.MemeAssure));
    }

    [Fact]
    public async Task Pas_de_candidate_le_volet_cree_la_vente_comme_avant()
    {
        var (cas, _, volets, _) = Cas();
        var p = await cas.ProposerAsync(Volet, 5);
        Assert.Empty(p.Candidates);
        // Le poste reprend alors le volet (circuit « Chifa d'abord ») : la lecture reste permise
        var lu = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(Volet, 5);
        Assert.Equal(18, lu.Lignes.Single().Produit!.Id);
    }

    [Fact]
    public async Task Concordance_rattachement_sans_motif_ni_droit_particulier()
    {
        var (cas, ventes, _, journal) = Cas();
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3));
        var r = await cas.RattacherAsync(Volet, 2, "motif ignoré", 5);
        var rat = Assert.Single(ventes.Rattaches);
        Assert.Equal(("06930126", (string?)null, new DateOnly(2026, 11, 9)), (rat.Qr.NumeroFacture, rat.Motif, rat.Qr.DateOrdonnance!.Value));
        Assert.False(r.AvecEcart);
        Assert.Equal("T-000002", r.Vente);
        Assert.Contains(RapprocherVoletChifa.Action, journal.Actions);
    }

    [Fact]
    public async Task Ecart_de_prix_sous_le_seuil_concorde_et_ecart_de_part_regularise_en_perte()
    {
        var (cas, ventes, _, journal) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3, prix: 160m, part: 384m));   // écart 3,52 DA < 5 DA
        // La caisse paiera 8,45 DA de moins : choix exigé
        var sansChoix = await Assert.ThrowsAsync<RegleMetierException>(() => cas.RattacherAsync(Volet, 2, null, 5));
        Assert.Equal("regularisation", sansChoix.Champ);
        Assert.Empty(ventes.Rattaches);

        var r = await cas.RattacherAsync(Volet, 2, null, 5, RegularisationPartOrganisme.PertePharmacie);
        Assert.False(r.AvecEcart);
        Assert.Equal(384m - 375.55m, r.EcartPartOrganisme);
        var rat = ventes.Rattaches.Single();
        Assert.Equal(8.45m, rat.EcartPartOrganisme);
        Assert.Equal((RegularisationPartOrganisme.PertePharmacie, 375.55m, 0m, -8.45m),
            (rat.Regularisation!.Sort, rat.Regularisation.PartOrganismeChifa, rat.Regularisation.MontantPatient, rat.Regularisation.MontantPharmacie));
        var i = journal.Actions.IndexOf(RapprocherVoletChifa.ActionRegularisation);
        Assert.Equal(RegularisationPartOrganisme.PertePharmacie, journal.Details[i]["sort"]);
        Assert.StartsWith("Perte pharmacie : 8", r.Regularisation);
    }

    [Fact]
    public async Task Caisse_paie_moins_choix_reserve_a_un_ayant_droit()
    {
        var (cas, ventes, _, _) = Cas("caisse.vendre");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3, part: 400m));
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() => cas.RattacherAsync(Volet, 2, null, 5, RegularisationPartOrganisme.ChargePatient));
        Assert.Equal(Pharma.Domain.Securite.CodeComptoir.ActionEcartVoletChifa, ex.Permission);   // « Faire autoriser » (D1)
        Assert.Empty(ventes.Rattaches);
    }

    [Fact]
    public async Task Caisse_paie_moins_a_la_charge_du_patient()
    {
        var (cas, ventes, _, _) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3, part: 400m));   // part Chifa 375,55 : 24,45 DA de moins
        var r = await cas.RattacherAsync(Volet, 2, null, 5, RegularisationPartOrganisme.ChargePatient);
        var g = ventes.Rattaches.Single().Regularisation!;
        Assert.Equal((RegularisationPartOrganisme.ChargePatient, 24.45m, 24.45m, 0m), (g.Sort, g.Ecart, g.MontantPatient, g.MontantPharmacie));
        Assert.Equal(12L, ventes.Rattaches.Single().PatientId);
        Assert.Contains("ajoutés à sa dette", r.Regularisation);
    }

    [Fact]
    public async Task Caisse_paie_plus_trop_paye_rendu_au_patient_dette_d_abord()
    {
        // Total 469,44 ; part enregistrée 300 ; patient a payé 169,44 ; part Chifa 375,55 : sa part Chifa 93,89, trop-payé 75,55
        var (cas, ventes, _, _) = Cas("caisse.vendre");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3, part: 300m) with { PartPatient = 169.44m, DettePatient = 20m });
        var r = await cas.RattacherAsync(Volet, 2, null, 5);   // aucun choix ni droit particulier
        var g = ventes.Rattaches.Single().Regularisation!;
        Assert.Equal((RegularisationPartOrganisme.AvoirPatient, -75.55m, 75.55m, 20m, 55.55m, 0m),
            (g.Sort, g.Ecart, g.MontantPatient, g.DetteDiminuee, g.ARendre, g.MontantPharmacie));
        Assert.Contains($"à rendre {55.55m:N2}", r.Regularisation);
    }

    [Theory]
    [InlineData(469.44, 0, 75.55, 0)]        // patient n'a rien payé (part Chifa ≥ sa part) : gain organisme entier
    [InlineData(469.44, 100, 75.55, 6.11)]   // a payé 100 pour une part Chifa de 93,89 : 6,11 rendus, 69,44 de gain
    public void Caisse_paie_plus_rendu_limite_au_trop_paye(decimal total, decimal partPatient, decimal surplus, decimal rendu)
    {
        var g = RegularisationPartOrganisme.Calculer(-surplus, 375.55m, total, partPatient, true, 0, null)!;
        Assert.Equal(rendu == 0 ? RegularisationPartOrganisme.GainOrganisme : RegularisationPartOrganisme.AvoirPatient, g.Sort);
        Assert.Equal((rendu, rendu, surplus - rendu), (g.MontantPatient, g.ARendre, g.MontantPharmacie));
    }

    [Fact]
    public void Regularisation_sans_patient_ni_ecart()
    {
        Assert.Null(RegularisationPartOrganisme.Calculer(0, 100, 200, 100, true, 0, null));
        var ex = Assert.Throws<RegleMetierException>(() => RegularisationPartOrganisme.Calculer(10, 100, 200, 90, false, 0, RegularisationPartOrganisme.ChargePatient));
        Assert.Equal("regularisation", ex.Champ);
        Assert.Equal(RegularisationPartOrganisme.PertePharmacie,
            RegularisationPartOrganisme.Calculer(10, 100, 200, 90, false, 0, RegularisationPartOrganisme.PertePharmacie)!.Sort);
    }

    [Fact]
    public async Task Ecarts_refuses_sans_le_droit_modifier_les_prix()
    {
        var (cas, ventes, _, _) = Cas("caisse.vendre");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 2));
        var ex = await Assert.ThrowsAsync<DroitRefuseException>(() => cas.RattacherAsync(Volet, 2, "Quantité corrigée par Chifa", 5));
        // Refus autorisable par le code d'un ayant droit « prix.modifier », pour cette seule action, même sur une connexion par code (D1)
        Assert.Equal(Pharma.Domain.Securite.CodeComptoir.ActionEcartVoletChifa, ex.Permission);
        Assert.True(Pharma.Domain.Securite.CodeComptoir.AutorisableParCollegue(ex.Permission));
        Assert.Equal("prix.modifier", Pharma.Domain.Securite.CodeComptoir.DroitDuValideur(ex.Permission));
        Assert.Empty(ventes.Rattaches);
    }

    [Fact]
    public async Task Ecarts_rattaches_avec_le_code_d_un_ayant_droit()
    {
        // Le code comptoir d'un collègue accorde l'action (PermissionsComptoir) : le rattachement et la régularisation passent
        var (cas, ventes, _, journal) = Cas("caisse.vendre", Pharma.Domain.Securite.CodeComptoir.ActionEcartVoletChifa);
        ventes.Ventes.Add(Vente(2, "690755003842/80", 2, part: 260m));   // part Chifa sur 2 boîtes servies : 250,37 ; la caisse paiera 9,63 de moins
        var r = await cas.RattacherAsync(Volet, 2, "Quantité corrigée par Chifa", 5, RegularisationPartOrganisme.PertePharmacie);
        Assert.True(r.AvecEcart);
        Assert.Equal(RegularisationPartOrganisme.PertePharmacie, ventes.Rattaches.Single().Regularisation!.Sort);
        Assert.Contains(RapprocherVoletChifa.ActionRegularisation, journal.Actions);
    }

    [Fact]
    public void Part_chifa_sur_la_quantite_servie_jamais_une_boite_non_servie()
    {
        // Décision du titulaire (2026-11-15) : volet de 3 boîtes, vente de 2 → part Chifa sur 2 boîtes (250,37 et non 375,55),
        // l'écart de quantité reste une différence en rouge
        var c = RapprochementVolet.Comparer([new LigneVoletRapprochee("07607", 3, 156.48m, 18, "VIT D3")],
            [new LigneVenteRapprochee(18, "VIT D3", 2, 156.48m)], 5m, 80, 250.37m);
        Assert.Equal(250.37m, c.PartOrganismeChifa);
        Assert.Equal(0m, c.EcartPartOrganisme);
        Assert.Equal(RapprochementVolet.Quantite, c.Lignes.Single().Ecart);
        Assert.False(c.Concorde);
        // Volet de 1 boîte, vente de 2 : la part reste celle du volet (l'organisme ne paie que ce qu'il a facturé)
        var moins = RapprochementVolet.Comparer([new LigneVoletRapprochee("07607", 1, 156.48m, 18, "VIT D3")],
            [new LigneVenteRapprochee(18, "VIT D3", 2, 156.48m)], 5m, 80, 250.37m);
        Assert.Equal(125.18m, moins.PartOrganismeChifa);
    }

    [Fact]
    public async Task Ecarts_avec_le_droit_motif_obligatoire_puis_rattachement_journalise()
    {
        var (cas, ventes, _, journal) = Cas("caisse.vendre", "prix.modifier");
        ventes.Ventes.Add(Vente(2, "690755003842/80", 2, part: 250.37m));   // part Chifa sur les 2 boîtes servies : aucun écart de part
        var sansMotif = await Assert.ThrowsAsync<RegleMetierException>(() => cas.RattacherAsync(Volet, 2, "  ", 5));
        Assert.Equal("motif", sansMotif.Champ);
        Assert.Empty(ventes.Rattaches);

        var r = await cas.RattacherAsync(Volet, 2, "  Quantité   corrigée par Chifa ", 5);
        Assert.True(r.AvecEcart);
        var rat = Assert.Single(ventes.Rattaches);
        Assert.Equal("Quantité corrigée par Chifa", rat.Motif);
        Assert.Contains("VIT D3 : 3 sur le volet, 2 servie(s)", rat.DetailEcarts);
        var i = journal.Actions.IndexOf(RapprocherVoletChifa.Action);
        Assert.Equal("Quantité corrigée par Chifa", journal.Details[i]["motif"]);
        Assert.Equal(false, journal.Details[i]["concorde"]);
    }

    [Fact]
    public async Task Volet_deja_repris_ou_rattache_refuse()
    {
        var (cas, ventes, volets, _) = Cas();
        ventes.Ventes.AddRange([Vente(2, "690755003842/80", 3), Vente(3, "690755003842/80", 3)]);
        volets.VenteExistante = "T-000042";
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ProposerAsync(Volet, 5));
        Assert.Contains("T-000042", ex.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RattacherAsync(Volet, 2, null, 5));
        Assert.Empty(ventes.Rattaches);

        // Rattaché entre-temps à une autre vente par un autre poste : refusé par l'unicité du volet
        volets.VenteExistante = null;
        await cas.RattacherAsync(Volet, 2, null, 5);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RattacherAsync(Volet, 3, null, 5));
        // Une vente ne reçoit qu'un volet : déjà rattachée, elle n'attend plus de volet
        var autre = await Assert.ThrowsAsync<RegleMetierException>(() =>
            cas.RattacherAsync("06930127;11/11/2026;690755003842;80;24;2;09/11/2026;07607,3,156.48;", 2, null, 5));
        Assert.Equal("vente", autre.Champ);
        Assert.Single(ventes.Rattaches);
    }

    [Fact]
    public async Task Catalogue_mis_a_jour_au_rattachement()
    {
        var (cas, ventes, volets, journal) = Cas();
        volets.Catalogue[18] = ("VIT D3", false, null, 160m);
        ventes.Ventes.Add(Vente(2, "690755003842/80", 3));
        var r = await cas.RattacherAsync(Volet, 2, null, 5);
        Assert.Equal((true, (decimal?)156.48m), (volets.Catalogue[18].Remboursable, volets.Catalogue[18].Tarif));
        Assert.StartsWith("Catalogue mis à jour d’après le volet Chifa n° 06930126", r.Catalogue);
        Assert.Contains(CatalogueVoletChifa.Action, journal.Actions);
    }

    [Fact]
    public async Task Vente_d_un_autre_assure_difference_en_rouge_et_refus_sans_droit()
    {
        var (cas, ventes, _, _) = Cas("caisse.vendre");
        ventes.Ventes.Add(Vente(3, "111111111111/01", 3));   // mêmes produits, autre assuré
        var p = await cas.ProposerAsync(Volet, 5);
        var c = Assert.Single(p.Candidates).Comparaison;
        Assert.False(c.Concorde);
        Assert.Equal(RapprochementVolet.AssureDifferent, c.Lignes[0].Ecart);
        Assert.Contains("690755003842/80", c.Lignes[0].Produit);
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.RattacherAsync(Volet, 3, "Carte de l’assuré principal", 5));
        Assert.Empty(ventes.Rattaches);
    }

    [Theory]
    [InlineData("690755003842/80", false)]   // même assuré : rien à journaliser
    [InlineData("690755003842 80", false)]   // autre séparateur
    [InlineData("690755003842/01", true)]    // ayant droit : autre assuré
    public async Task Patient_choisi_garde_malgre_un_volet_d_un_autre_assure_journalise(string numPatient, bool journalise)
    {
        var journal = new JournalEspion();
        var r = await new GarderPatientVoletChifa(new DroitsFactices("caisse.vendre"), journal).ExecuterAsync(Volet, 7, "Benali Sara", numPatient, 5);
        Assert.Equal(journalise, r);
        Assert.Equal(journalise ? [GarderPatientVoletChifa.Action] : [], journal.Actions);
        Assert.Equal("Le volet appartient à l’assuré 690755003842/80, pas à Benali Sara.", AssureVolet.Avertissement("690755003842/80", "Benali Sara"));
    }

    [Fact]
    public void Comparaison_produits_absents_et_code_non_associe_en_rouge()
    {
        var c = RapprochementVolet.Comparer(
            [new LigneVoletRapprochee("07607", 1, 100m, 18, "VIT D3"), new LigneVoletRapprochee("09999", 1, 50m, null, null),
             new LigneVoletRapprochee("01234", 2, 80m, 40, "Doliprane")],
            [new LigneVenteRapprochee(18, "VIT D3", 1, 100m), new LigneVenteRapprochee(55, "Smecta", 1, 300m)], 5m, 80, 100m);
        Assert.Equal([RapprochementVolet.Concorde, RapprochementVolet.CodeNonAssocie, RapprochementVolet.AbsentVente, RapprochementVolet.AbsentVolet],
            c.Lignes.Select(l => l.Ecart));
        Assert.False(c.Concorde);
        Assert.Equal(80m + 40m, c.PartOrganismeChifa);   // Doliprane non servi : aucune part (quantité servie, 2026-11-15)
        Assert.Equal(3 - 1 - 1 - 1, c.Score);
    }
}
