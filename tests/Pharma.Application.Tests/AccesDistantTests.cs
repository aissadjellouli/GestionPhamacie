using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Commun;
using Pharma.Application.Securite;
using Pharma.Application.Synchronisation;
using Pharma.Domain.Commun;
using Pharma.Domain.Securite;
using Pharma.Shared;

namespace Pharma.Application.Tests;

/// <summary>
/// Lot S7.2b : Administration › Synchronisation › Accès à distance (sans réseau ni base) : titulaire seulement, mot de passe ressaisi,
/// refus clair sans synchronisation activée, refus neutre et limite du serveur central, code jamais journalisé.
/// </summary>
public class AccesDistantTests
{
    private const string BonMotDePasse = "secret";
    private static readonly Guid Officine = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>Faux dépôt de synchronisation : seuls les paramètres sont lus (toute autre méthode : NotSupportedException).</summary>
    public class DepotParametres : DispatchProxy
    {
        public ParametresEnvoiSync Parametres { get; set; } = null!;
        protected override object? Invoke(MethodInfo? m, object?[]? args) =>
            m?.Name == nameof(IEnvoiSyncDepot.ParametresAsync) ? Task.FromResult(Parametres) : throw new NotSupportedException(m?.Name);
    }

    private sealed class Certificat(CertificatSyncInfo? info) : ICertificatSync
    {
        public CertificatSyncInfo? Lire() => info;
        public string NouvelleDemande() => throw new NotSupportedException();
        public void Installer(string certificatPem, string autoritePem, string? epinglage) => throw new NotSupportedException();
        public X509Certificate2? CertificatClient() => null;
        public X509Certificate2? Autorite() => null;
    }

    private sealed class Central(Func<string, CodeRattachementSync>? reponse = null) : IClientServeurCentral
    {
        public readonly List<(string Adresse, string Compte)> Appels = [];
        public Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AccuseLotSync> DeposerAsync(string adresse, byte[] lot, long versionDescendante, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<CodeRattachementSync> CodeRattachementAsync(string adresse, string compte, CancellationToken ct = default)
        {
            Appels.Add((adresse, compte));
            return Task.FromResult((reponse ?? (_ => new CodeRattachementSync("ABCD-EFGH-JKLM", DateTime.UtcNow.AddMinutes(30))))(compte));
        }

        // Lot S7.3 : comptes rattachés, droits, retrait
        public readonly List<string> AppelsComptes = [];
        public Exception? Panne { get; init; }
        public Task<IReadOnlyList<CompteDistantSync>> ComptesDistantsAsync(string adresse, CancellationToken ct = default)
        {
            AppelsComptes.Add("liste");
            if (Panne is not null) throw Panne;
            return Task.FromResult<IReadOnlyList<CompteDistantSync>>([new CompteDistantSync("titulaire.dupont", "Dupont", ["consulter", "ordres"], DateTime.UtcNow, true)]);
        }
        public Task<int> DroitsCompteDistantAsync(string adresse, string compte, IReadOnlyList<string> droits, CancellationToken ct = default)
        {
            AppelsComptes.Add($"droits:{compte}:{string.Join(",", droits)}");
            if (Panne is not null) throw Panne;
            return Task.FromResult(droits.Contains("ordres") ? 0 : 1);
        }
        public Task<int> RetirerCompteDistantAsync(string adresse, string compte, CancellationToken ct = default)
        {
            AppelsComptes.Add($"retrait:{compte}");
            if (Panne is not null) throw Panne;
            return Task.FromResult(2);
        }
    }

    private static (ComptesAccesDistant Cas, Central Central, Journal Journal) CreerComptes(ParametresEnvoiSync? p = null, bool titulaire = true,
        Central? central = null)
    {
        var depot = DispatchProxy.Create<IEnvoiSyncDepot, DepotParametres>();
        ((DepotParametres)(object)depot).Parametres = p ?? Parametres();
        var s = central ?? new Central();
        var j = new Journal();
        return (new ComptesAccesDistant(depot, new Certificat(CertificatValable), new Droits(titulaire),
            new VerificationMotDePasseActuel(new Comptes(), new Verificateur(), j), s, j), s, j);
    }

    [Fact]
    public async Task S7_3_comptes_rattaches_reserves_au_titulaire_et_synchronisation_activee()
    {
        var (cas, central, _) = CreerComptes();
        Assert.Equal("titulaire.dupont", Assert.Single(await cas.ListerAsync(1)).Identifiant);
        var (employe, c2, _) = CreerComptes(titulaire: false);
        await Assert.ThrowsAsync<DroitRefuseException>(() => employe.ListerAsync(2));
        await Assert.ThrowsAsync<DroitRefuseException>(() => employe.ModifierDroitsAsync("titulaire.dupont", ["ordres"], BonMotDePasse, 2));
        await Assert.ThrowsAsync<DroitRefuseException>(() => employe.RetirerAsync("titulaire.dupont", BonMotDePasse, 2));
        Assert.Empty(c2.AppelsComptes);
        var (inactive, c3, _) = CreerComptes(Parametres(active: false));
        Assert.Equal(GenererCodeAccesDistant.RefusNonActivee, (await Assert.ThrowsAsync<RegleMetierException>(() => inactive.ListerAsync(1))).Message);
        Assert.Empty(c3.AppelsComptes);
        Assert.Single(central.AppelsComptes);
    }

    [Fact]
    public async Task S7_3_droits_et_retrait_exigent_le_mot_de_passe_et_sont_journalises()
    {
        var (cas, central, journal) = CreerComptes();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierDroitsAsync("titulaire.dupont", ["consulter", "ordres"], "faux", 1));
        Assert.Equal(GenererCodeAccesDistant.ChampMotDePasse, e.Champ);
        await Assert.ThrowsAsync<RegleMetierException>(() => cas.RetirerAsync("titulaire.dupont", null, 1));
        Assert.Empty(central.AppelsComptes);
        // Droits hors liste ou vides : refusés avant le mot de passe et l'appel
        Assert.Equal(ComptesAccesDistant.ChampDroits, (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierDroitsAsync("titulaire.dupont", ["admin"], BonMotDePasse, 1))).Champ);
        Assert.Equal(ComptesAccesDistant.ChampDroits, (await Assert.ThrowsAsync<RegleMetierException>(() => cas.ModifierDroitsAsync("titulaire.dupont", [], BonMotDePasse, 1))).Champ);
        Assert.Empty(central.AppelsComptes);

        Assert.Equal(1, await cas.ModifierDroitsAsync(" Titulaire.Dupont ", ["journal_acces", "consulter", "consulter"], BonMotDePasse, 1));
        Assert.Equal(2, await cas.RetirerAsync("titulaire.dupont", BonMotDePasse, 1));
        Assert.Equal(["droits:titulaire.dupont:consulter,journal_acces", "retrait:titulaire.dupont"], central.AppelsComptes);
        var ecrits = journal.Ecrits.Where(x => x.Action is "droits_acces_distant" or "retrait_acces_distant").ToList();
        Assert.Equal(2, ecrits.Count);
        Assert.Equal("consulter,journal_acces", ecrits[0].Details["droits"]);
        Assert.Equal(2, ecrits[1].Details["ordres_annules"]);
        Assert.DoesNotContain(journal.Ecrits.SelectMany(x => x.Details.Values), v => v is string t && t.Contains(BonMotDePasse));
    }

    [Fact]
    public async Task S7_3_compte_non_rattache_et_central_injoignable_rendus_clairement()
    {
        foreach (var (panne, attendu) in new (Exception, string)[]
                 {
                     (new ServeurCentralException("serveur central : 422", 422), ComptesAccesDistant.RefusInconnu),
                     (new ServeurCentralException("serveur central : 502", 502), ComptesAccesDistant.RefusInjoignable),
                     (new HttpRequestException("coupé"), ComptesAccesDistant.RefusInjoignable),
                 })
        {
            var (cas, _, journal) = CreerComptes(central: new Central { Panne = panne });
            Assert.Equal(attendu, (await Assert.ThrowsAsync<RegleMetierException>(() => cas.RetirerAsync("titulaire.dupont", BonMotDePasse, 1))).Message);
            Assert.DoesNotContain(journal.Ecrits, x => x.Action == "retrait_acces_distant");
        }
    }

    private sealed class Droits(bool titulaire) : IPermissionService
    {
        public Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(0m);
        public Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(titulaire);
    }

    private sealed class Journal : IJournal
    {
        public readonly List<(string Action, IReadOnlyDictionary<string, object?> Details)> Ecrits = [];
        public Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId,
            IReadOnlyDictionary<string, object?> details, string? permissionCode, CancellationToken ct = default)
        {
            Ecrits.Add((action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class Comptes : IComptesLecture
    {
        public Task<Utilisateur?> TrouverActifAsync(string identifiant, CancellationToken ct = default) => Task.FromResult<Utilisateur?>(null);
        public Task NoterConnexionAsync(long utilisateurId, string? poste, string methode, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Utilisateur?> ParIdAsync(long utilisateurId, CancellationToken ct = default) =>
            Task.FromResult<Utilisateur?>(new Utilisateur { Id = utilisateurId, Identifiant = $"compte{utilisateurId}", NomComplet = "Compte", MotDePasseHash = "h:" + BonMotDePasse });
        public Task ChangerMotDePasseAsync(long utilisateurId, string hash, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Verificateur : IVerificateurMotDePasse
    {
        public bool Verifier(string motDePasse, string hash) => hash == "h:" + motDePasse;
    }

    private static ParametresEnvoiSync Parametres(bool active = true, string? adresse = "https://central.exemple:5090", DateTime? inscrite = null,
        bool lectureSeule = false) =>
        new(active, Officine, 1, ContratSync.VersionV1, 0, 0, adresse, inscrite ?? DateTime.UtcNow.AddDays(-1), null, null, null, null, "filtres", false, 0, 0,
            BaseEnLectureSeule: lectureSeule);

    private static readonly CertificatSyncInfo CertificatValable = new("e".PadRight(64, 'e'), DateTime.UtcNow.AddYears(1), "a".PadRight(64, 'a'));

    private static (GenererCodeAccesDistant Cas, Central Central, Journal Journal) Creer(ParametresEnvoiSync? p = null, CertificatSyncInfo? c = null,
        bool titulaire = true, Central? central = null, bool sansCertificat = false)
    {
        var depot = DispatchProxy.Create<IEnvoiSyncDepot, DepotParametres>();
        ((DepotParametres)(object)depot).Parametres = p ?? Parametres();
        var s = central ?? new Central();
        var j = new Journal();
        var cas = new GenererCodeAccesDistant(depot, new Certificat(sansCertificat ? null : c ?? CertificatValable), new Droits(titulaire),
            new VerificationMotDePasseActuel(new Comptes(), new Verificateur(), j), s, j);
        return (cas, s, j);
    }

    [Fact]
    public async Task Titulaire_et_bon_mot_de_passe_code_rendu_une_fois_jamais_journalise()
    {
        var (cas, central, journal) = Creer();
        var r = await cas.ExecuterAsync(" Titulaire.Dupont ", BonMotDePasse, 1);
        Assert.Equal("ABCD-EFGH-JKLM", r.Code);
        Assert.Equal(("https://central.exemple:5090", "titulaire.dupont"), Assert.Single(central.Appels));
        var (action, details) = Assert.Single(journal.Ecrits);
        Assert.Equal("code_acces_distant", action);
        Assert.DoesNotContain(details.Values, v => v is string t && (t.Contains("ABCD") || t.Contains("titulaire.dupont") || t.Contains(BonMotDePasse)));
    }

    [Fact]
    public async Task Reserve_au_titulaire()
    {
        var (cas, central, _) = Creer(titulaire: false);
        await Assert.ThrowsAsync<DroitRefuseException>(() => cas.ExecuterAsync("titulaire.dupont", BonMotDePasse, 2));
        Assert.Empty(central.Appels);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("faux")]
    public async Task Mot_de_passe_du_titulaire_requis(string? motDePasse)
    {
        var (cas, central, journal) = Creer();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("titulaire.dupont", motDePasse, 1));
        Assert.Equal(GenererCodeAccesDistant.ChampMotDePasse, e.Champ);
        Assert.Empty(central.Appels);
        Assert.Equal("mot_de_passe_actuel_refuse", Assert.Single(journal.Ecrits).Action);
    }

    [Fact]
    public async Task Refus_clair_sans_synchronisation_activee()
    {
        foreach (var (p, sansCert, echu) in new[]
                 {
                     (Parametres(active: false), false, false), (Parametres(adresse: null), false, false),
                     (Parametres() with { InscriteLe = null }, false, false), (Parametres(), true, false), (Parametres(), false, true),
                 })
        {
            var (cas, central, _) = Creer(p, echu ? CertificatValable with { EcheanceUtc = DateTime.UtcNow.AddDays(-1) } : null, sansCertificat: sansCert);
            var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("titulaire.dupont", BonMotDePasse, 1));
            Assert.Equal(GenererCodeAccesDistant.RefusNonActivee, e.Message);
            Assert.Empty(central.Appels);
        }
        var (lecture, c2, _) = Creer(Parametres(lectureSeule: true));
        Assert.Equal(GenererCodeAccesDistant.RefusLectureSeule,
            (await Assert.ThrowsAsync<RegleMetierException>(() => lecture.ExecuterAsync("titulaire.dupont", BonMotDePasse, 1))).Message);
        Assert.Empty(c2.Appels);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("pas valide")]
    [InlineData(".commence.par.un.point")]
    public async Task Identifiant_mal_forme_refuse_avant_tout_appel(string compte)
    {
        var (cas, central, _) = Creer();
        var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync(compte, BonMotDePasse, 1));
        Assert.Equal(GenererCodeAccesDistant.ChampCompte, e.Champ);
        Assert.Empty(central.Appels);
    }

    [Theory]
    [InlineData("titulaire.dupont")]
    [InlineData(" Titulaire.Dupont ")]
    [InlineData("ab")]
    [InlineData("a_b-c.d")]
    [InlineData("pas valide")]
    [InlineData("-abc")]
    [InlineData("")]
    public void Meme_controle_de_l_identifiant_que_le_serveur_central(string compte) =>
        Assert.Equal(ComptesDistants.IdentifiantValide(ComptesDistants.NormaliserIdentifiant(compte)) ? ComptesDistants.NormaliserIdentifiant(compte) : null,
            GenererCodeAccesDistant.NormaliserCompte(compte));

    [Fact]
    public async Task Refus_neutre_limite_et_central_injoignable_rendus_clairement_sans_journal()
    {
        foreach (var (exception, attendu) in new (Exception, string)[]
                 {
                     (new ServeurCentralException("serveur central : 422 refus", 422), GenererCodeAccesDistant.RefusNeutre),
                     (new ServeurCentralException("serveur central : 429 trop", 429, quota: true), GenererCodeAccesDistant.RefusLimite),
                     (new ServeurCentralException("serveur central : 429", 429), GenererCodeAccesDistant.RefusLimite),
                     (new ServeurCentralException("serveur central : 502", 502), GenererCodeAccesDistant.RefusInjoignable),
                     (new HttpRequestException("coupé"), GenererCodeAccesDistant.RefusInjoignable),
                 })
        {
            var (cas, _, journal) = Creer(central: new Central(_ => throw exception));
            var e = await Assert.ThrowsAsync<RegleMetierException>(() => cas.ExecuterAsync("titulaire.dupont", BonMotDePasse, 1));
            Assert.Equal(attendu, e.Message);
            Assert.Empty(journal.Ecrits);
        }
    }
}
