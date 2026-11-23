using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Domain.Commun;

namespace Pharma.Application.Tests;

internal sealed class CodesEnMemoire : ICodesComptoir
{
    public Dictionary<long, CompteComptoir> Comptes { get; } = [];

    public Task<CompteComptoir?> TrouverActifAsync(string identifiant, CancellationToken ct = default) =>
        Task.FromResult(Comptes.Values.FirstOrDefault(c => c.Identifiant == identifiant));
    public Task<CompteComptoir?> ParIdAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Comptes.GetValueOrDefault(id));
    public Task EnregistrerEssaiAsync(long id, int essais, DateTime? bloqueJusqu, CancellationToken ct = default)
    {
        Comptes[id] = Comptes[id] with { Essais = essais, BloqueJusqu = bloqueJusqu };
        return Task.CompletedTask;
    }
    public Task<int?> ReserverEssaiAsync(long id, DateTime maintenant, CancellationToken ct = default)
    {
        var c = Comptes[id];
        if (c.BloqueJusqu is { } b && b > maintenant) return Task.FromResult<int?>(null);
        var n = c.Essais + 1;
        Comptes[id] = c with { Essais = n, BloqueJusqu = Pharma.Domain.Securite.CodeComptoir.DureeBlocage(n) is { } d ? maintenant + d : null };
        return Task.FromResult<int?>(n);
    }
    public Task<CompteComptoir?> TrouverActifParIdAsync(long id, CancellationToken ct = default) =>
        Task.FromResult(Comptes.GetValueOrDefault(id));
    public Task DefinirAsync(long id, string? hash, CancellationToken ct = default)
    {
        Comptes[id] = Comptes[id] with { CodeHash = hash, Essais = 0, BloqueJusqu = null };
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<CompteComptoir>> AvecCodeAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CompteComptoir>>(Comptes.Values.Where(c => c.CodeHash is not null).ToList());
}

public class ConnexionComptoirTests
{
    private static readonly DateOnly Jour = new(2026, 11, 9);

    private static (SeConnecterParCode Cas, CodesEnMemoire Codes, JetonsFactices Jetons, JournalEspion Journal) Monter(
        SessionsEnMemoire? sessions = null, DroitsFactices? droits = null)
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var jetons = new JetonsFactices();
        var journal = new JournalEspion();
        var cas = new SeConnecterParCode(codes, new VerificateurFactice(), jetons, sessions ?? new SessionsEnMemoire(),
            droits ?? new DroitsFactices("caisse.vendre"), new ComptesEnMemoire(), new HorlogeFixe(Jour), journal);
        return (cas, codes, jetons, journal);
    }

    [Fact]
    public async Task Bon_code_sur_un_poste_ouvert_donne_un_jeton_limite_au_comptoir()
    {
        var (cas, _, jetons, _) = Monter();
        var session = await cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1");
        Assert.Equal("Vendeur B", session.NomComplet);
        Assert.True(jetons.Comptoir.Single());
    }

    [Fact]
    public async Task Code_refuse_si_la_caisse_du_poste_est_fermee()
    {
        var (cas, _, _, _) = Monter(new SessionsEnMemoire { Ouverte = null });
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Contains("mot de passe", ex.Message);
    }

    [Fact]
    public async Task Cinq_codes_faux_bloquent_meme_le_bon_code()
    {
        var (cas, codes, _, journal) = Monter();
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.NotNull(codes.Comptes[9].BloqueJusqu);
        Assert.Contains("code_comptoir_bloque", journal.Actions);
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Contains("bloqué", ex.Message);
    }

    // Blocage expiré simulé : l'horloge des tests est fixe
    private static void Expirer(CodesEnMemoire codes) => codes.Comptes[9] = codes.Comptes[9] with { BloqueJusqu = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) };

    [Fact]
    public async Task Le_blocage_double_apres_chaque_blocage_sans_remettre_le_compteur_a_un()
    {
        var (cas, codes, _, _) = Monter();
        var debut = new HorlogeFixe(Jour).MaintenantUtc;
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.Equal(debut.AddMinutes(5), codes.Comptes[9].BloqueJusqu);
        Expirer(codes);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.Equal(6, codes.Comptes[9].Essais);
        Assert.Equal(debut.AddMinutes(10), codes.Comptes[9].BloqueJusqu);
        Expirer(codes);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.Equal(debut.AddMinutes(20), codes.Comptes[9].BloqueJusqu);
    }

    [Fact]
    public async Task Au_dela_du_seuil_chaque_blocage_est_signale_au_titulaire()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var alertes = new AlertesTitulaireEspion();
        var verif = new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(Jour), new JournalEspion(), alertes);
        for (var i = 1; i <= Pharma.Domain.Securite.CodeComptoir.SeuilAlerte; i++)
        {
            Expirer(codes);
            await Assert.ThrowsAsync<RegleMetierException>(() => verif.VerifierAsync("vendeur.b", "1111", "Caisse 1"));
            if (i < Pharma.Domain.Securite.CodeComptoir.SeuilAlerte) Assert.Empty(alertes.Surveillances);
        }
        var alerte = Assert.Single(alertes.Surveillances);
        Assert.True(alerte.Urgent);
        Assert.Contains("Vendeur B", alerte.Message);
    }

    [Fact]
    public async Task Un_valideur_se_designe_par_sa_reference_sans_son_identifiant()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var verif = new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(Jour), new JournalEspion());
        var compte = await verif.VerifierAsync(VerificationCodeComptoir.Reference(9), "2580", "Caisse 1", default, demandeurId: 3);   // demandeur connecté seulement
        Assert.Equal(9, compte.Id);
    }

    [Theory]
    [InlineData("role.titulaire")]
    [InlineData("admin.utilisateurs")]
    [InlineData("tresorerie.gerer")]
    [InlineData("psychotropes.delivrer")]
    [InlineData(null)]
    public async Task Valideurs_jamais_proposes_hors_liste_blanche(string? permission)
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "titulaire", "Titulaire", "hash:2580", 0, null);
        var lister = new ListerValidateurs(codes, new DroitsFactices("role.titulaire", "admin.utilisateurs", "tresorerie.gerer", "psychotropes.delivrer") { Titulaire = true });
        Assert.Empty(await lister.AutorisablesAsync(permission, 1, null));
    }

    [Fact]
    public async Task Valideurs_proposes_pour_un_droit_de_la_liste_blanche_avec_leur_numero()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        var v = Assert.Single(await new ListerValidateurs(codes, new DroitsFactices("caisse.remise")).AutorisablesAsync("caisse.remise", 1, null));
        Assert.Equal(9, v.Id);
    }

    [Fact]
    public async Task Un_bon_code_remet_les_essais_a_zero()
    {
        var (cas, codes, _, _) = Monter();
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "1111", "Caisse 1"));
        Assert.Equal(1, codes.Comptes[9].Essais);
        await cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1");
        Assert.Equal(0, codes.Comptes[9].Essais);
    }

    [Fact]
    public async Task Sans_droit_de_vendre_pas_de_reprise_de_caisse()
    {
        var (cas, _, _, _) = Monter(droits: new DroitsFactices("stock.consulter"));
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
    }

    [Fact]
    public async Task Employe_sans_code_refuse_avec_le_meme_message()
    {
        var (cas, codes, _, _) = Monter();
        codes.Comptes[9] = codes.Comptes[9] with { CodeHash = null };
        var ex = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("vendeur.b", "2580", "Caisse 1"));
        Assert.Equal(SeConnecterParCode.Refus, ex.Message);
    }

    [Fact]
    public async Task Changer_son_code_exige_le_mot_de_passe()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", null, 0, null);
        var definir = new DefinirCodeComptoir(codes, new HacheurFactice(), new JournalEspion());
        await Assert.ThrowsAsync<RegleMetierException>(() => definir.ExecuterAsync(9, connexionParCode: true, "2580"));
        await definir.ExecuterAsync(9, connexionParCode: false, "2580");
        Assert.Equal("hash:2580", codes.Comptes[9].CodeHash);
    }

    [Fact]
    public async Task Effacer_un_code_exige_l_administration_des_utilisateurs()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[9] = new CompteComptoir(9, "vendeur.b", "Vendeur B", "hash:2580", 0, null);
        await Assert.ThrowsAsync<DroitRefuseException>(() =>
            new EffacerCodeComptoir(codes, new DroitsFactices("caisse.vendre"), new JournalEspion()).ExecuterAsync(9, 5));
        await new EffacerCodeComptoir(codes, new DroitsFactices("admin.utilisateurs"), new JournalEspion()).ExecuterAsync(9, 5);
        Assert.Null(codes.Comptes[9].CodeHash);
    }

    [Fact]
    public async Task Codes_faux_d_un_meme_demandeur_bloquent_le_demandeur_pas_le_valideur_revue_I2()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[1] = new CompteComptoir(1, "titulaire", "Titulaire", "hash:2580", 0, null);
        var (alertes, journal, demandeurs) = (new AlertesTitulaireEspion(), new JournalEspion(), new EssaisCodeDemandeur());
        var verif = new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(Jour), journal, alertes, demandeurs);
        for (var i = 0; i < EssaisCodeDemandeur.EssaisMax; i++)
            await Assert.ThrowsAsync<RegleMetierException>(() => verif.VerifierAsync("#1", "1111", "Caisse 1", default, demandeurId: 7));
        // Le titulaire n'est pas bloqué ; c'est le demandeur qui l'est, journalisé et signalé
        Assert.Equal((0, (DateTime?)null), (codes.Comptes[1].Essais, codes.Comptes[1].BloqueJusqu));
        Assert.Contains("code_comptoir_demandeur_bloque", journal.Actions);
        Assert.Single(alertes.Surveillances);
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => verif.VerifierAsync("#1", "2580", "Caisse 1", default, demandeurId: 7));
        Assert.Contains("bloquée pour vous", e.Message);
        // Un autre employé fait toujours valider par le titulaire, et le titulaire se connecte toujours par son code
        Assert.Equal(1, (await verif.VerifierAsync("#1", "2580", "Caisse 1", default, demandeurId: 8)).Id);
        Assert.Equal(1, (await verif.VerifierAsync("titulaire", "2580", "Caisse 1")).Id);
    }

    [Fact]
    public async Task Codes_faux_de_plusieurs_demandeurs_comptent_au_valideur_revue_I2()
    {
        var codes = new CodesEnMemoire();
        codes.Comptes[1] = new CompteComptoir(1, "titulaire", "Titulaire", "hash:2580", 0, null);
        var verif = new VerificationCodeComptoir(codes, new VerificateurFactice(), new HorlogeFixe(Jour), new JournalEspion(), null, new EssaisCodeDemandeur());
        await Assert.ThrowsAsync<RegleMetierException>(() => verif.VerifierAsync("#1", "1111", "Caisse 1", default, demandeurId: 7));
        Assert.Equal(0, codes.Comptes[1].Essais);
        await Assert.ThrowsAsync<RegleMetierException>(() => verif.VerifierAsync("#1", "1111", "Caisse 1", default, demandeurId: 8));
        Assert.Equal(1, codes.Comptes[1].Essais);
    }
}
