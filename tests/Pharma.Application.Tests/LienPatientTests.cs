using Pharma.Application.Caisse;
using Pharma.Application.Patients;
using Pharma.Domain.Commun;
using Pharma.Domain.Ventes;

namespace Pharma.Application.Tests;

/// <summary>Fiches de patients en mémoire, avec lien et assuré principal (script 090).</summary>
internal sealed class FichesFamille(params FichePatient[] fiches) : IPatients
{
    public List<FichePatient> Fiches { get; } = [.. fiches];
    public Task<IReadOnlyList<PatientResume>> RechercherAsync(string t, int l, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<PatientResume>>([]);
    public Task<Patient?> TrouverAsync(long id, CancellationToken ct = default) => Task.FromResult<Patient?>(null);
    public Task<long> CreerAsync(NouveauPatient p, long? creePar, CancellationToken ct = default) => Task.FromResult(99L);
    public Task<FichePatient?> FicheAsync(long id, CancellationToken ct = default) => Task.FromResult(Fiches.FirstOrDefault(f => f.Id == id));
    public Task<bool> ModifierAsync(FichePatient f, CancellationToken ct = default)
    {
        var i = Fiches.FindIndex(x => x.Id == f.Id);
        if (i < 0) return Task.FromResult(false);
        Fiches[i] = f;
        return Task.FromResult(true);
    }
    public Task<IReadOnlyList<ProchePatient>> ProchesAsync(long id, CancellationToken ct = default)
    {
        var moi = Fiches.Single(f => f.Id == id);
        var b = FamillePatient.NumeroBase(moi.NumAssure);
        return Task.FromResult<IReadOnlyList<ProchePatient>>(Fiches
            .Where(p => p.Id != id && (p.Id == moi.AssurePrincipalId || p.AssurePrincipalId == id || (b is not null && FamillePatient.NumeroBase(p.NumAssure) == b)))
            .Select(p => new ProchePatient(p.Id, $"{p.Nom} {p.Prenom}".Trim(), p.NumAssure, p.Lien, p.AssurePrincipalId)).ToList());
    }
}

public class LienPatientTests
{
    private static readonly DateOnly Aujourdhui = new(2026, 11, 15);
    private static FichePatient F(long id, string nom, string? num, string? lien = null, long? principal = null) =>
        new(id, nom, null, null, null, null, num, 1, false, 0, 0, lien, principal);

    private static readonly FichePatient Ali = F(1, "Ali", "690755003842/00", FamillePatient.Assure);
    private static readonly FichePatient Fatima = F(2, "Fatima", "690755003842/01");
    private static readonly FichePatient Karim = F(3, "Karim", "690755003842/02");

    private static Task Modifier(FichesFamille patients, FichePatient saisie, JournalEspion? journal = null) =>
        new ModifierPatient(patients, journal ?? new JournalEspion(), new HorlogeFixe(Aujourdhui)).ExecuterAsync(saisie, 3);

    [Fact]
    public async Task Rattachement_valide_enregistre_et_journalise()
    {
        var patients = new FichesFamille(Ali, Fatima);
        var journal = new JournalEspion();
        await Modifier(patients, Fatima with { Lien = " Conjoint ", AssurePrincipalId = 1 }, journal);
        var f = patients.Fiches.Single(x => x.Id == 2);
        Assert.Equal(("conjoint", (long?)1), (f.Lien, f.AssurePrincipalId));
        Assert.Equal(["assure_principal_id", "lien"], journal.Details[0].Keys.Order());
    }

    [Fact]
    public async Task Lien_hors_liste_refuse_et_vide_accepte()
    {
        var patients = new FichesFamille(Ali, Fatima);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(patients, Fatima with { Lien = "cousin" }));
        Assert.Equal(FamillePatient.ChampLien, e.Champ);
        await Modifier(patients, Fatima with { Lien = "  " });
        Assert.Null(patients.Fiches.Single(x => x.Id == 2).Lien);
    }

    [Fact]
    public async Task Patient_ne_peut_pas_etre_son_propre_assure_principal()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(new FichesFamille(Ali, Fatima), Fatima with { AssurePrincipalId = 2 }));
        Assert.Contains("propre assuré principal", e.Message);
    }

    [Fact]
    public async Task Lien_assure_sans_assure_principal()
    {
        var e = await Assert.ThrowsAsync<RegleMetierException>(() =>
            Modifier(new FichesFamille(Ali, Fatima), Fatima with { Lien = "assure", AssurePrincipalId = 1 }));
        Assert.Equal(FamillePatient.ChampPrincipal, e.Champ);
    }

    [Fact]
    public async Task Pas_de_chaine_l_assure_principal_doit_etre_un_assure()
    {
        // Fatima est conjointe d'Ali : Karim ne peut pas être rattaché à elle
        var patients = new FichesFamille(Ali, Fatima with { Lien = "conjoint", AssurePrincipalId = 1 }, Karim);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(patients, Karim with { Lien = "enfant", AssurePrincipalId = 2 }));
        Assert.Contains("ayant droit", e.Message);
        // Ni un patient sans lien mais lui-même rattaché
        patients = new FichesFamille(Ali, Fatima with { AssurePrincipalId = 1 }, Karim);
        await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(patients, Karim with { AssurePrincipalId = 2 }));
    }

    [Fact]
    public async Task Assure_avec_ayants_droit_ne_devient_pas_ayant_droit()
    {
        var patients = new FichesFamille(Ali, Fatima with { Lien = "conjoint", AssurePrincipalId = 1 }, Karim);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(patients, Ali with { Lien = null, AssurePrincipalId = 3 }));
        Assert.Contains("assuré principal de Fatima", e.Message);
        await Assert.ThrowsAsync<RegleMetierException>(() => Modifier(patients, Ali with { Lien = "enfant" }));
    }

    [Fact]
    public async Task Fiche_sans_changement_de_lien_non_controlee()
    {
        // Fiche existante sans lien : le reste se modifie sans contrôle du lien
        var patients = new FichesFamille(Ali, Fatima);
        await Modifier(patients, Fatima with { Nom = "Fatima B." });
        Assert.Equal("Fatima B.", patients.Fiches.Single(x => x.Id == 2).Nom);
    }

    private static ProchePatient P(FichePatient f) => new(f.Id, f.Nom, f.NumAssure, f.Lien, f.AssurePrincipalId);

    [Fact]
    public void Proposition_de_rattachement_par_numero_de_base()
    {
        var autreCarte = F(4, "Omar", "111111111111/00", FamillePatient.Assure);
        var p = FamillePatient.Proposition(2, Fatima.NumAssure, null, null, [P(Karim), P(Ali), P(autreCarte)]);
        Assert.Equal(1, p!.Id);                                                                  // Ali (lien « assure ») d'abord
        Assert.Null(FamillePatient.Proposition(2, Fatima.NumAssure, null, 1, [P(Ali)]));          // déjà rattachée
        Assert.Null(FamillePatient.Proposition(1, Ali.NumAssure, "assure", null, [P(Fatima)]));   // l'assuré lui-même
        Assert.Null(FamillePatient.Proposition(2, Fatima.NumAssure, null, null, [P(autreCarte)])); // autre n° de base
        Assert.Null(FamillePatient.Proposition(2, Fatima.NumAssure, null, null,
            [P(Ali with { Lien = "conjoint" })]));                                                // un ayant droit n'est pas proposé
        Assert.Equal("690755003842", FamillePatient.NumeroBase("690755003842 80"));
        Assert.Null(FamillePatient.NumeroBase("12345"));
    }

    [Fact]
    public void Affichage_famille_ayant_droit_et_meme_carte()
    {
        var fatima = Fatima with { Lien = "conjoint", AssurePrincipalId = 1 };
        var karim = Karim with { Lien = "enfant", AssurePrincipalId = 1 };
        var inconnu = F(5, "Assuré 690755003842/05", "690755003842/05");
        var ali = FamillePatient.Affichage(1, Ali.NumAssure, null, [P(fatima), P(karim), P(inconnu)]);
        Assert.Equal([("Famille", "Fatima (conjoint), Karim (enfant)"), ("Même carte", "Assuré 690755003842/05 (690755003842/05)")], ali);
        var f = FamillePatient.Affichage(2, fatima.NumAssure, 1, [P(Ali), P(karim)]);
        Assert.Equal([("Ayant droit de", "Ali")], f);
    }

    // ---- Scan d'un volet : recherche stricte n° + suffixe ----
    private static CandidatVolet C(FichePatient f) => new(f.Id, f.Nom, f.NumAssure!, "CNAS", 1, f.Lien, f.AssurePrincipalId);

    [Fact]
    public void Scan_recherche_stricte_ayant_droit_non_confondu_avec_l_assure()
    {
        var candidats = new[] { C(Ali) };
        Assert.Null(PatientScanVolet.Patient(candidats, "690755003842", "01"));        // ayant droit /01 : pas Ali
        Assert.Equal(1, PatientScanVolet.Patient(candidats, "690755003842", "00")!.Id);
        Assert.Equal(1, PatientScanVolet.Patient([C(Ali with { NumAssure = "690755003842 00" })], "690755003842", "00")!.Id);
        // N° sans suffixe dans la fiche : plus de repli sur le n° seul
        Assert.Null(PatientScanVolet.Patient([C(Ali with { NumAssure = "690755003842" })], "690755003842", "80"));
    }

    [Fact]
    public void Scan_assure_principal_propose_fiche_de_meme_base_au_lien_assure()
    {
        Assert.Equal(1, PatientScanVolet.AssurePrincipalPropose([C(Karim), C(Ali)], "690755003842", "01")!.Id);
        Assert.Null(PatientScanVolet.AssurePrincipalPropose([C(Ali with { Lien = null })], "690755003842", "01"));   // rien déduit du suffixe
        Assert.Null(PatientScanVolet.AssurePrincipalPropose([C(Ali)], "690755003842", "00"));                         // c'est lui
    }

    private const string VoletAyantDroit = "06930127;11/11/2026;690755003899;01;24;2;09/11/2026;07607,3,156.48;";

    [Fact]
    public async Task Volet_ayant_droit_inconnu_assure_principal_propose_puis_creation()
    {
        var volets = new VoletsChifaEnMemoire { PrincipalPropose = (1L, "Ali") };
        var v = await new LireVoletChifa(volets, new DroitsFactices("caisse.vendre")).ExecuterAsync(VoletAyantDroit, 3);
        Assert.Null(v.PatientId);
        Assert.Equal((1L, "Ali"), (v.AssurePrincipalProposeId!.Value, v.AssurePrincipalPropose));

        var patients = new FichesFamille(Ali with { NumAssure = "690755003899/00" });
        var journal = new JournalEspion();
        var p = await new CreerPatientVoletChifa(volets, new DroitsFactices("caisse.vendre"), journal, patients)
            .ExecuterAsync(VoletAyantDroit, "Fatima", null, null, 1, 3);
        Assert.True(p.Cree);
        Assert.Equal((null, (long?)1), volets.DerniereFamille);

        // Assuré principal qui est lui-même un ayant droit : refusé
        patients = new FichesFamille(Ali with { Lien = "conjoint", AssurePrincipalId = 9 });
        await Assert.ThrowsAsync<RegleMetierException>(() => new CreerPatientVoletChifa(volets, new DroitsFactices("caisse.vendre"), journal, patients)
            .ExecuterAsync("06930128;11/11/2026;690755003899;02;24;2;09/11/2026;07607,3,156.48;", "Karim", null, "enfant", 1, 3));
    }
}
