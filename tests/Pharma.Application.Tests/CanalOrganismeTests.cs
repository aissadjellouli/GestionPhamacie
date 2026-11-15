using Pharma.Application.Caisse;
using Pharma.Application.Factures;
using Pharma.Application.Patients;
using Pharma.Domain.Commun;
using Pharma.Domain.Factures;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Ventes Chifa et MDN jamais refacturées par le logiciel (script 089, 2026-11-15).</summary>
public class CanalOrganismeTests
{
    private const string Volet = "06930126;11/11/2026;690755003842;80;24;2;09/11/2026;07607,2,350.00;";

    private static (Encaisser Cas, VentesEspion Ventes) CasCaisse(VoletsChifaEnMemoire? volets = null, Referentiels.IMotifsReglables? motifs = null)
    {
        volets ??= new VoletsChifaEnMemoire();
        volets.Codes["07607"] = new ProduitChifa(2, "Amoxicilline", 350);
        var ventes = new VentesEspion();
        var cas = new Encaisser(new CatalogueEnMemoire(), new PatientsEnMemoire(), ventes, new SessionsEnMemoire(), new DroitsFactices("caisse.vendre"),
            new HorlogeFixe(CatalogueEnMemoire.Aujourdhui), new ScansEnMemoire(), voletsChifa: volets, motifs: motifs);
        return (cas, ventes);
    }

    private static CommandeVente TiersPayant(long organisme, bool factureeChifa = false, string? numero = null, string? motif = "carte_absente",
        string? texte = null) =>
        new([new LigneCommande(2, 20, 1)], true, organisme, 80, 12, new OrdonnanceSaisie(1, CatalogueEnMemoire.Aujourdhui, null), 0,
            ModePaiement.Especes, null, FactureeParChifa: factureeChifa, NumeroFactureChifa: numero, MotifSansVoletCode: motif, MotifSansVoletTexte: texte);

    /// <summary>Liste réglable « volet_absent » (script 097) : motifs de départ, un motif désactivé.</summary>
    private static MotifsEnMemoire MotifsVoletAbsent()
    {
        var m = new MotifsEnMemoire();
        m.Motifs.AddRange([
            new(101, "volet_absent", "carte_absente", "Pas de carte Chifa présentée", true, false, 10),
            new(102, "volet_absent", "chifa_indisponible", "CHIFA-Officine indisponible", false, false, 20),
            new(103, "volet_absent", "autre", "Autre (préciser)", true, true, 999)
        ]);
        return m;
    }

    [Fact]
    public async Task Facturee_par_Chifa_sans_numero_motif_enregistre_sur_la_vente()
    {
        var (cas, ventes) = CasCaisse(motifs: MotifsVoletAbsent());
        await cas.ExecuterAsync(TiersPayant(1, true, null, "autre", "  Patient pressé "), 5, "Caisse 1");
        Assert.Equal(("autre", "Patient pressé"), (ventes.Recu!.MotifSansVoletCode, ventes.Recu.MotifSansVoletTexte));
    }

    [Theory]
    [InlineData(null, null, "motif")]                    // aucun motif
    [InlineData("chifa_indisponible", null, "motif")]    // motif désactivé
    [InlineData("autre", "  ", "motif_texte")]           // « Autre » sans texte
    public async Task Facturee_par_Chifa_sans_numero_refusee_sans_motif_valable(string? motif, string? texte, string champ)
    {
        var (cas, ventes) = CasCaisse(motifs: MotifsVoletAbsent());
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(TiersPayant(1, true, null, motif, texte), 5, "Caisse 1"));
        Assert.Equal(champ, ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Facturee_par_Chifa_avec_numero_aucun_motif_demande()
    {
        var (cas, ventes) = CasCaisse(motifs: MotifsVoletAbsent());
        await cas.ExecuterAsync(TiersPayant(1, true, "06931126", motif: null), 5, "Caisse 1");
        Assert.Equal(("06931126", (string?)null), (ventes.Recu!.FactureChifa, ventes.Recu.MotifSansVoletCode));
    }

    [Fact]
    public async Task Vente_reprise_d_un_volet_canal_chifa()
    {
        var (cas, ventes) = CasCaisse();
        await cas.ExecuterAsync(new CommandeVente([new LigneCommande(2, 20, 2)], true, 1, 80, 12, null, 0, ModePaiement.Especes, null, VoletChifa: Volet),
            5, "Caisse 1");
        Assert.Equal((CanalOrganisme.Chifa, (string?)null), (ventes.Recu!.CanalOrganisme, ventes.Recu.FactureChifa));
    }

    [Fact]
    public async Task Vente_a_l_organisme_MDN_canal_mdn()
    {
        var (cas, ventes) = CasCaisse();
        await cas.ExecuterAsync(TiersPayant(4), 5, "Caisse 1");
        Assert.Equal(CanalOrganisme.Mdn, ventes.Recu!.CanalOrganisme);
    }

    [Fact]
    public async Task Vente_CNAS_ordinaire_canal_logiciel()
    {
        var (cas, ventes) = CasCaisse();
        await cas.ExecuterAsync(TiersPayant(1), 5, "Caisse 1");
        Assert.Equal(CanalOrganisme.Logiciel, ventes.Recu!.CanalOrganisme);
    }

    [Fact]
    public async Task Vente_facturee_par_Chifa_sans_volet_canal_chifa_avec_le_numero()
    {
        var (cas, ventes) = CasCaisse();
        await cas.ExecuterAsync(TiersPayant(2, true, " 06931126 "), 5, "Caisse 1");
        Assert.Equal((CanalOrganisme.Chifa, "06931126"), (ventes.Recu!.CanalOrganisme, ventes.Recu.FactureChifa));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Facturee_par_Chifa_sans_numero_vente_en_attente_de_volet(string? numero)
    {
        // Circuit « la caisse d'abord » (2026-11-15) : la vente est encaissée, canal chifa, sans n° (le volet sera rattaché ensuite)
        var (cas, ventes) = CasCaisse();
        await cas.ExecuterAsync(TiersPayant(1, true, numero), 5, "Caisse 1");
        Assert.Equal((CanalOrganisme.Chifa, (string?)null, "carte_absente"), (ventes.Recu!.CanalOrganisme, ventes.Recu.FactureChifa, ventes.Recu.MotifSansVoletCode));
    }

    [Theory]
    [InlineData(1L, "0123456789012345678901234567890", "facture_chifa")]   // 31 caractères : trop long
    [InlineData(4L, "06931126", "organisme")]   // CNAS ou CASNOS seulement
    public async Task Facturee_par_Chifa_refusee_sans_numero_ou_hors_CNAS_CASNOS(long organisme, string? numero, string champ)
    {
        var (cas, ventes) = CasCaisse();
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(TiersPayant(organisme, true, numero), 5, "Caisse 1"));
        Assert.Equal(champ, ex.Champ);
        Assert.Null(ventes.Recu);
    }

    [Fact]
    public async Task Facture_Chifa_deja_rattachee_a_une_vente_refusee()
    {
        var (cas, _) = CasCaisse(new VoletsChifaEnMemoire { VenteExistante = "T-000012" });
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(TiersPayant(1, true, "06931126"), 5, "Caisse 1"));
        Assert.Contains("T-000012", ex.Message);
    }

    [Theory]
    [InlineData(false, false, false, "CNAS", "logiciel")]
    [InlineData(true, true, false, "CNAS", "chifa")]
    [InlineData(true, false, true, "CASNOS", "chifa")]
    [InlineData(true, false, false, "mdn", "mdn")]
    [InlineData(true, false, false, "CNAS", "logiciel")]
    [InlineData(false, false, false, "MDN", "logiciel")]   // sans tiers payant : rien à facturer à l'organisme
    public void Canal_determine(bool tiers, bool volet, bool coche, string code, string attendu) =>
        Assert.Equal(attendu, CanalOrganisme.Determiner(tiers, volet, coche, code));

    // ---- Factures d'organisme ----

    private static Facturation Facturation(FacturesEnMemoire f) =>
        new(f, new DroitsFactices("factures.gerer"), new HorlogeFixe(new DateOnly(2026, 11, 9)), new JournalEspion());

    private static FacturesEnMemoire VenteExterne(string canal)
    {
        var f = new FacturesEnMemoire();
        f.Lignes[0] = f.Lignes[0] with { Canal = canal };
        return f;
    }

    [Theory]
    [InlineData("chifa", "Chifa")]
    [InlineData("mdn", "MDN")]
    public async Task Facture_d_organisme_refusee_pour_une_vente_Chifa_ou_MDN(string canal, string attendu)
    {
        var f = VenteExterne(canal);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Facturation(f).EmettreAsync(new DemandeFacture("organisme", null, null, 1, null, null, null, [1], [], null), 5));
        Assert.Contains($"facturée par {(canal == "mdn" ? "le MDN" : "Chifa")}", ex.Message);
        Assert.Contains(attendu, ex.Message);
        Assert.Empty(f.Emises);
    }

    [Fact]
    public async Task Part_patient_d_une_vente_Chifa_toujours_facturable_au_patient()
    {
        var f = VenteExterne(CanalOrganisme.Chifa);
        await Facturation(f).EmettreAsync(new DemandeFacture("patient", 3, null, null, null, null, null, [1], [], null), 5);
        Assert.Single(f.Emises);
        Assert.Equal(Destinataire.Patient, f.Emises[0].Destinataire);
    }

    // ---- Un seul patient par n° d'assuré (n° + suffixe) ----

    private sealed class PatientsAssures(params (long Id, string Nom, string NumAssure)[] fiches) : IPatients
    {
        public List<NouveauPatient> Crees { get; } = [];
        public FichePatient? Fiche { get; set; }
        public Task<IReadOnlyList<PatientResume>> RechercherAsync(string t, int l, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatientResume>>([]);
        public Task<Patient?> TrouverAsync(long id, CancellationToken ct = default) => Task.FromResult<Patient?>(null);
        public Task<long> CreerAsync(NouveauPatient p, long? creePar, CancellationToken ct = default) { Crees.Add(p); return Task.FromResult(99L); }
        public Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default) => Task.FromResult(Fiche?.Id == id ? Fiche : null);
        public Task<bool> ModifierAsync(FichePatient f, CancellationToken ct = default) => Task.FromResult(true);
        public Task<(long Id, string Nom)?> AssureUtiliseAsync(string chiffres, long? sauf, CancellationToken ct = default) =>
            Task.FromResult(fiches.Where(f => f.Id != sauf && RegleNumAssure.Chiffres(f.NumAssure) == chiffres)
                .Select(f => ((long Id, string Nom)?)(f.Id, f.Nom)).FirstOrDefault());
    }

    [Theory]
    [InlineData("690755003842/80")]
    [InlineData("690755003842 80")]
    [InlineData(" 69075500384280 ")]
    public async Task Creation_refusee_meme_numero_meme_suffixe(string saisie)
    {
        var patients = new PatientsAssures((12, "Benali Amine", "690755003842/80"));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() =>
            new CreerPatient(patients).ExecuterAsync(new NouveauPatient("Autre", null, null, saisie, 1, 0)));
        Assert.Equal(("num_assure", true), (ex.Champ, ex.Message.StartsWith("N° d’assuré déjà utilisé par Benali Amine", StringComparison.Ordinal)));
        Assert.Empty(patients.Crees);
    }

    [Fact]
    public async Task Ayant_droit_meme_numero_autre_suffixe_accepte()
    {
        var patients = new PatientsAssures((12, "Benali Amine", "690755003842/80"));
        await new CreerPatient(patients).ExecuterAsync(new NouveauPatient("Benali", "Sara", null, "690755003842/01", 1, 0));
        Assert.Equal("690755003842/01", Assert.Single(patients.Crees).NumAssure);
    }

    [Fact]
    public async Task Modification_refusee_si_le_numero_est_celui_d_un_autre_patient_acceptee_pour_le_sien()
    {
        var fiche = new FichePatient(7, "Benali", "Karim", null, null, null, "111111111111/80", 1, false, 0);
        var patients = new PatientsAssures((12, "Benali Amine", "690755003842/80"), (7, "Benali Karim", "111111111111/80")) { Fiche = fiche };
        var cas = new ModifierPatient(patients, new JournalEspion(), new HorlogeFixe(new DateOnly(2026, 11, 15)));
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(fiche with { NumAssure = "690755003842/80" }, 3));
        Assert.Equal("num_assure", ex.Champ);
        await cas.ExecuterAsync(fiche with { NumAssure = "111111111111 80", Telephone = null }, 3);   // son propre n°, autre séparateur
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("  ", null)]
    [InlineData("ABC", null)]
    [InlineData("690755003842/80", "69075500384280")]
    public void Numero_reduit_a_ses_chiffres(string? saisie, string? attendu) => Assert.Equal(attendu, RegleNumAssure.Chiffres(saisie));
}
