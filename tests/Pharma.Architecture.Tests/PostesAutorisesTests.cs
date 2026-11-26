using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.Notifications;
using Pharma.Application.Postes;
using Pharma.Application.Securite;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Liste des postes autorisés (docs/taches/2026-11-24-postes-autorises/contrat-api.md) : chaîne signée commune au poste et au serveur, vérification des preuves
/// (horloge, nonce, révocation, remplacement), intergiciel selon le mode, contrôle du jeton, enrôlement par code et gestion par le titulaire.
/// </summary>
public class PostesAutorisesTests
{
    // ---- Doublures ---------------------------------------------------------------------------------------------------------------------

    internal sealed class Horloge : IHorloge
    {
        public DateTime MaintenantUtc { get; set; } = new(2026, 11, 24, 9, 0, 0, DateTimeKind.Utc);
        public DateOnly Aujourdhui => DateOnly.FromDateTime(MaintenantUtc);
        public long Unix => (long)MaintenantUtc.Subtract(DateTime.UnixEpoch).TotalSeconds;
    }

    internal sealed class Depot : IPostesAutorisesDepot, Pharma.Application.Offre.ILimitesOffre
    {
        public sealed class Ligne
        {
            public long Id; public string Nom = ""; public string Statut = "en_attente"; public byte[]? Cle; public string? Empreinte; public int Generation = 1;
            public byte[]? Code; public DateTime? Expire; public int Essais; public long? CreePar; public string? Motif; public DateTime? Revoque;
        }

        public List<Ligne> Lignes { get; } = [];
        public ReglagesPostes Reglages { get; set; } = ReglagesPostes.ParDefaut;
        /// <summary>Maximum de postes de l'offre signée (script 172).</summary>
        public int Maximum { get; set; } = 20;
        public Task<int> MaximumPostesAsync(CancellationToken ct = default) => Task.FromResult(Maximum);
        public HashSet<string> Occupes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Panne { get; set; }
        public int LecturesCles { get; private set; }
        public Func<DateTime> Maintenant { get; set; } = () => DateTime.UtcNow;
        private long suivant = 1;

        private void Verifier() { if (Panne) throw new InvalidOperationException("base illisible"); }

        public Task<ReglagesPostes> ReglagesAsync(CancellationToken ct = default) { Verifier(); return Task.FromResult(Reglages); }
        public Task EnregistrerReglagesAsync(ReglagesPostes reglages, CancellationToken ct = default) { Reglages = reglages; return Task.CompletedTask; }

        public Task<PosteCle?> LireCleAsync(long id, CancellationToken ct = default)
        {
            Verifier();
            LecturesCles++;
            var l = Lignes.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(l is null ? null : new PosteCle(l.Id, l.Nom, l.Statut, l.Cle, l.Generation));
        }

        public Task<IReadOnlySet<string>> NomsReservesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlySet<string>>(Lignes.Where(l => l.Statut != "revoque").Select(l => l.Nom.ToLowerInvariant()).ToHashSet());

        public Task<Page<PosteAutoriseLu>> ListerAsync(string? statut, string? recherche, DemandePage page, CancellationToken ct = default) =>
            Task.FromResult(new Page<PosteAutoriseLu>(Lignes.Where(l => statut is null || l.Statut == statut).Select(Lu).ToList(), Lignes.Count, page.Page, page.Taille));

        public Task<PosteAutoriseLu?> LireAsync(long id, CancellationToken ct = default) =>
            Task.FromResult(Lignes.FirstOrDefault(l => l.Id == id) is { } l ? Lu(l) : null);

        public Task<int> NombreNonRevoquesAsync(CancellationToken ct = default) => Task.FromResult(Lignes.Count(l => l.Statut != "revoque"));

        public Task<CreationPoste> CreerAsync(string nom, byte[] codeHache, DateTime expireLe, long creePar, int maximum, CancellationToken ct = default)
        {
            if (Lignes.Count(l => l.Statut != "revoque") >= maximum) return Task.FromResult(new CreationPoste(null, MaximumAtteint: true));
            if (Lignes.Any(l => l.Statut != "revoque" && string.Equals(l.Nom, nom, StringComparison.OrdinalIgnoreCase))) return Task.FromResult(new CreationPoste(null));
            var l = new Ligne { Id = suivant++, Nom = nom, Code = codeHache, Expire = expireLe, CreePar = creePar };
            Lignes.Add(l);
            return Task.FromResult(new CreationPoste(l.Id));
        }

        public Task<bool> NouveauCodeAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default)
        {
            if (Lignes.FirstOrDefault(x => x.Id == id && x.Statut == "en_attente") is not { } l) return Task.FromResult(false);
            (l.Code, l.Expire, l.Essais) = (codeHache, expireLe, 0);
            return Task.FromResult(true);
        }

        public Task<bool> RemplacerAsync(long id, byte[] codeHache, DateTime expireLe, CancellationToken ct = default)
        {
            if (Lignes.FirstOrDefault(x => x.Id == id && x.Statut == "actif") is not { } l) return Task.FromResult(false);
            (l.Statut, l.Cle, l.Empreinte, l.Generation, l.Code, l.Expire, l.Essais) = ("en_attente", null, null, l.Generation + 1, codeHache, expireLe, 0);
            return Task.FromResult(true);
        }

        public Task<bool?> RenommerAsync(long id, string nom, CancellationToken ct = default)
        {
            if (Lignes.FirstOrDefault(x => x.Id == id && x.Statut != "revoque") is not { } l) return Task.FromResult<bool?>(null);
            if (Lignes.Any(x => x.Id != id && x.Statut != "revoque" && string.Equals(x.Nom, nom, StringComparison.OrdinalIgnoreCase))) return Task.FromResult<bool?>(false);
            l.Nom = nom;
            return Task.FromResult<bool?>(true);
        }

        public Task<bool> RevoquerAsync(long id, long revoquePar, string motif, CancellationToken ct = default)
        {
            if (Lignes.FirstOrDefault(x => x.Id == id && x.Statut != "revoque") is not { } l) return Task.FromResult(false);
            (l.Statut, l.Motif, l.Revoque, l.Code) = ("revoque", motif, Maintenant(), null);
            return Task.FromResult(true);
        }

        public Task<EnrolementLu?> EnrolerAsync(byte[] codeHache, byte[] clePublique, string empreinte, string? ordinateur, int essaisMax, CancellationToken ct = default)
        {
            var l = Lignes.FirstOrDefault(x => x.Statut == "en_attente" && x.Code is not null && x.Code.SequenceEqual(codeHache) && x.Expire > Maintenant() && x.Essais < essaisMax);
            if (l is null) return Task.FromResult<EnrolementLu?>(null);
            (l.Statut, l.Cle, l.Empreinte, l.Code, l.Expire, l.Essais) = ("actif", clePublique, empreinte, null, null, 0);
            return Task.FromResult<EnrolementLu?>(new EnrolementLu(l.Id, l.Nom, l.CreePar));
        }

        public Task<int> NoterEchecCodeAsync(CancellationToken ct = default)
        {
            var attente = Lignes.Where(l => l.Statut == "en_attente").ToList();
            foreach (var l in attente) l.Essais++;
            return Task.FromResult(attente.Count == 0 ? 0 : attente.Max(l => l.Essais));
        }

        public Task<bool> PosteOccupeAsync(string nom, CancellationToken ct = default) => Task.FromResult(Occupes.Contains(nom));
        public Task<IReadOnlyList<NomSansPreuve>> NomsSansPreuveAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<NomSansPreuve>>([]);
        public Task NoterPreuveAsync(long id, CancellationToken ct = default) => Task.CompletedTask;

        private static PosteAutoriseLu Lu(Ligne l) => new(l.Id, l.Nom, l.Statut, l.Generation, DateTime.UtcNow, null, null, null, l.Empreinte, l.Expire, l.Revoque, l.Motif, l.CreePar);

        /// <summary>Poste actif avec une nouvelle clé (rendue pour signer).</summary>
        public (Ligne Ligne, ECDsa Cle) Actif(string nom, int generation = 1)
        {
            var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var spki = cle.ExportSubjectPublicKeyInfo();
            var l = new Ligne { Id = suivant++, Nom = nom, Statut = "actif", Cle = spki, Empreinte = PreuvePoste.EmpreinteCle(spki), Generation = generation, CreePar = 1 };
            Lignes.Add(l);
            return (l, cle);
        }
    }

    private sealed class Droits : IPermissionService
    {
        public bool Titulaire { get; set; } = true;
        public Task<bool> APermissionAsync(long utilisateurId, string permission, CancellationToken ct = default) => Task.FromResult(true);
        public Task<bool> EstTitulaireAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(Titulaire);
        public Task<string> LibelleAsync(string permission, CancellationToken ct = default) => Task.FromResult(permission);
        public Task<IReadOnlyList<string>> ListerAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<decimal> RemiseMaxAsync(long utilisateurId, CancellationToken ct = default) => Task.FromResult(0m);
    }

    private sealed class Journal : IJournal
    {
        public List<(long Par, string Action, IReadOnlyDictionary<string, object?> Details)> Entrees { get; } = [];
        public Task EcrireAsync(long utilisateurId, string action, string? tableCible, long? enregistrementId, IReadOnlyDictionary<string, object?> details,
            string? permissionCode, CancellationToken ct = default)
        {
            Entrees.Add((utilisateurId, action, details));
            return Task.CompletedTask;
        }
    }

    private sealed class Alertes : IAlertesTitulaire
    {
        public List<string> Messages { get; } = [];
        public Task SurveillanceAsync(string message, bool urgent, CancellationToken ct = default) { Messages.Add(message); return Task.CompletedTask; }
        public Task EcartCaisseAsync(string employe, string poste, decimal ecart, decimal seuilCaisse, CancellationToken ct = default, bool releve = false) => Task.CompletedTask;
        public Task LotBloqueAsync(string produit, string numeroLot, int boitesEnStock, int boitesVendues, CancellationToken ct = default) => Task.CompletedTask;
        public Task SauvegardeEnEchecAsync(bool disqueAbsent, string detail, CancellationToken ct = default) => Task.CompletedTask;
        public Task EcartsInventaireAsync(string rayon, int lignes, int boites, decimal valeur, CancellationToken ct = default) => Task.CompletedTask;
        public Task AnnulationsAsync(string poste, int annulations, int seuil, CancellationToken ct = default) => Task.CompletedTask;
        public Task LotDelaisseAsync(string message, bool urgent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static EnTetesPreuve Signer(ECDsa cle, long id, string methode, string cible, long horodatage, string? authorization = null, string? nonce = null)
    {
        nonce ??= PreuvePoste.NouveauNonce();
        var preuve = PreuvePoste.Signer(cle, PreuvePoste.Chaine(methode, cible, horodatage, nonce, id, authorization));
        return new EnTetesPreuve(id.ToString(), horodatage.ToString(), nonce, preuve);
    }

    // ---- Chaîne commune ------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void La_chaine_signee_suit_le_vecteur_du_contrat()
    {
        var chaine = PreuvePoste.Chaine("get", "/api/caisse/session?x=1", 1767225600, "AAAAAAAAAAAAAAAAAAAAAA", 7, "Bearer abc");
        Assert.Equal("PHARMA-POSTE-V1\nGET\n/api/caisse/session?x=1\n1767225600\nAAAAAAAAAAAAAAAAAAAAAA\n7\n"
                     + "c355dce96c1612880d11940ffdd9014d386c253e0c3652a6cd06a7226f7bd2b6", chaine);
        Assert.EndsWith("\n7\n", PreuvePoste.Chaine("GET", "/", 1, "n", 7, null));
    }

    [Fact]
    public void Les_constantes_de_l_application_sont_celles_du_poste()
    {
        Assert.Equal(PreuvePoste.ModeInactif, ConstantesPostes.ModeInactif);
        Assert.Equal(PreuvePoste.ModeTransition, ConstantesPostes.ModeTransition);
        Assert.Equal(PreuvePoste.ModeExige, ConstantesPostes.ModeExige);
        Assert.Equal(PreuvePoste.NomPostePris, ConstantesPostes.NomPostePris);
        Assert.Equal(PreuvePoste.PosteOccupe, ConstantesPostes.PosteOccupe);
        foreach (var code in new[] { "k7pq-3mzx", " K7PQ 3MZX ", "K7PQ3MZX" })
            Assert.Equal(PreuvePoste.NormaliserCode(code), ConstantesPostes.NormaliserCode(code));
    }

    [Fact]
    public void Le_code_d_enrolement_n_a_que_des_caracteres_non_ambigus()
    {
        for (var i = 0; i < 200; i++)
        {
            var code = CodeEnrolement.Nouveau();
            Assert.Matches("^[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}-[23456789ABCDEFGHJKMNPQRSTUVWXYZ]{4}$", code);
        }
        Assert.Equal(CodeEnrolement.Hacher("abcd-efgh"), CodeEnrolement.Hacher("ABCDEFGH"));
    }

    [Fact]
    public void Une_cle_autre_que_P256_est_refusee()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.Null(PreuvePoste.ClePublique(p384.ExportSubjectPublicKeyInfo()));
        using var rsa = RSA.Create(2048);
        Assert.Null(PreuvePoste.ClePublique(rsa.ExportSubjectPublicKeyInfo()));
        using var p256 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var lue = PreuvePoste.ClePublique(p256.ExportSubjectPublicKeyInfo());
        Assert.NotNull(lue);
    }

    // ---- Vérification ----------------------------------------------------------------------------------------------------------------------

    private static (VerificateurPreuvePoste Verif, Depot Depot, Horloge Horloge, RegistrePostesAutorises Registre) Banc()
    {
        var horloge = new Horloge();
        var depot = new Depot { Maintenant = () => horloge.MaintenantUtc };
        var registre = new RegistrePostesAutorises(depot, horloge);
        return (new VerificateurPreuvePoste(registre, horloge), depot, horloge, registre);
    }

    [Fact]
    public async Task Une_preuve_valide_donne_le_poste_et_son_nom_de_la_table()
    {
        var (verif, depot, horloge, _) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var r = await verif.VerifierAsync(Signer(cle, ligne.Id, "POST", "/api/caisse/vente", horloge.Unix, "Bearer x"), "POST", "/api/caisse/vente", "Bearer x");
        Assert.Equal(IssuePreuve.Valide, r.Issue);
        Assert.Equal(new PosteProuve(ligne.Id, "CAISSE-1"), r.Poste);
    }

    [Fact]
    public async Task La_preuve_lie_methode_cible_et_jeton()
    {
        var (verif, depot, horloge, _) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "POST", "/api/a", null)).Issue);
        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/b", null)).Issue);
        // Jeton recopié sur un autre poste : la preuve porte sur l'empreinte du jeton d'origine
        Assert.Equal(IssuePreuve.NonProuve,
            (await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix, "Bearer un"), "GET", "/api/a", "Bearer deux")).Issue);
        // Clé d'un autre poste
        var (_, autreCle) = depot.Actif("CAISSE-2");
        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(Signer(autreCle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null)).Issue);
    }

    [Fact]
    public async Task Un_nonce_rejoue_est_refuse()
    {
        var (verif, depot, horloge, _) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var e = Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix);
        Assert.Equal(IssuePreuve.Valide, (await verif.VerifierAsync(e, "GET", "/api/a", null)).Issue);
        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(e, "GET", "/api/a", null)).Issue);
    }

    [Fact]
    public async Task Une_horloge_hors_de_la_fenetre_de_120_secondes_est_signalee()
    {
        var (verif, depot, horloge, _) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        Assert.Equal(IssuePreuve.Valide, (await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix - 119), "GET", "/api/a", null)).Issue);
        var r = await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix + 121), "GET", "/api/a", null);
        Assert.Equal(IssuePreuve.Horloge, r.Issue);
        Assert.Equal(PreuvePoste.PreuveHorloge, r.Code);
    }

    [Fact]
    public async Task Poste_revoque_ou_remplace_est_refuse_comme_revoque_et_poste_inconnu_comme_non_prouve()
    {
        var (verif, depot, horloge, registre) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        ligne.Statut = "revoque";
        registre.Invalider(ligne.Id);
        Assert.Equal(IssuePreuve.Revoque, (await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null)).Issue);

        var (remplace, ancienne) = depot.Actif("CAISSE-2");
        (remplace.Statut, remplace.Cle, remplace.Generation) = ("en_attente", null, 2);
        Assert.Equal(IssuePreuve.Revoque, (await verif.VerifierAsync(Signer(ancienne, remplace.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null)).Issue);

        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(Signer(cle, 999, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null)).Issue);
        Assert.Equal(IssuePreuve.NonProuve, (await verif.VerifierAsync(new EnTetesPreuve("abc", "1", "x", "y"), "GET", "/api/a", null)).Issue);
        Assert.Equal(IssuePreuve.Absente, (await verif.VerifierAsync(new EnTetesPreuve(null, null, null, null), "GET", "/api/a", null)).Issue);
    }

    [Fact]
    public async Task Les_cles_sont_gardees_30_secondes_et_relues_apres_une_invalidation()
    {
        var (verif, depot, horloge, registre) = Banc();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        for (var i = 0; i < 5; i++) await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null);
        Assert.Equal(1, depot.LecturesCles);
        horloge.MaintenantUtc = horloge.MaintenantUtc.AddSeconds(31);
        await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null);
        Assert.Equal(2, depot.LecturesCles);
        registre.Invalider(ligne.Id);
        await verif.VerifierAsync(Signer(cle, ligne.Id, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null);
        Assert.Equal(3, depot.LecturesCles);
    }

    [Fact]
    public async Task Base_illisible_le_mode_retombe_a_inactif_et_la_preuve_est_indisponible()
    {
        var (verif, depot, horloge, registre) = Banc();
        depot.Panne = true;
        Assert.Equal(PreuvePoste.ModeInactif, await registre.ModeAsync());
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(IssuePreuve.Indisponible, (await verif.VerifierAsync(Signer(cle, 1, "GET", "/api/a", horloge.Unix), "GET", "/api/a", null)).Issue);
    }

    // ---- Intergiciel et contrôle du jeton --------------------------------------------------------------------------------------------------

    private static (DefaultHttpContext Ctx, Func<Task<bool>> Passer) Requete(Depot depot, Horloge horloge, string chemin, EnTetesPreuve? preuve = null,
        string? enTetePoste = null, ClaimsPrincipal? user = null, string methode = "GET")
    {
        var registre = new RegistrePostesAutorises(depot, horloge);
        var services = new ServiceCollection()
            .AddSingleton(registre)
            .AddSingleton(new VerificateurPreuvePoste(registre, horloge))
            .AddSingleton<IAlertesTitulaire>(new Alertes())
            .AddSingleton(new AlertesPostes(horloge))
            .AddLogging()
            .BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Request.Method = methode;
        ctx.Request.Path = chemin;
        ctx.Response.Body = new MemoryStream();
        if (preuve is not null)
        {
            ctx.Request.Headers[PreuvePoste.EnTeteId] = preuve.Id;
            ctx.Request.Headers[PreuvePoste.EnTeteHorodatage] = preuve.Horodatage;
            ctx.Request.Headers[PreuvePoste.EnTeteNonce] = preuve.Nonce;
            ctx.Request.Headers[PreuvePoste.EnTetePreuve] = preuve.Preuve;
        }
        if (enTetePoste is not null) ctx.Request.Headers[PorteeConnexion.EnTetePoste] = Uri.EscapeDataString(enTetePoste);
        if (user is not null) ctx.User = user;
        async Task<bool> Passer()
        {
            var passe = false;
            var mw = new PreuvePosteMiddleware(_ => { passe = true; return Task.CompletedTask; }, NullLogger<PreuvePosteMiddleware>.Instance);
            await mw.InvokeAsync(ctx);
            if (!passe) return false;
            var controle = false;
            await ControlePosteJeton.FiltrerAsync(ctx, () => { controle = true; return Task.CompletedTask; });
            return controle;
        }
        return (ctx, Passer);
    }

    private static ClaimsPrincipal Connecte(string? poste = null, long? posteId = null)
    {
        var c = new List<Claim> { new(ClaimTypes.NameIdentifier, "5") };
        if (poste is not null) c.Add(new Claim(PorteeConnexion.RevendicationPoste, poste));
        if (posteId is not null) c.Add(new Claim(PorteeConnexion.RevendicationPosteId, posteId.Value.ToString()));
        return new ClaimsPrincipal(new ClaimsIdentity(c, "Bearer"));
    }

    [Fact]
    public async Task Mode_inactif_rien_n_est_verifie_et_la_regle_d_avant_s_applique()
    {
        var horloge = new Horloge();
        var depot = new Depot { Reglages = ReglagesPostes.Inactif };
        var (ctx, passer) = Requete(depot, horloge, "/api/caisse", new EnTetesPreuve("1", "1", "x", "faux"), enTetePoste: "CAISSE-1");
        Assert.True(await passer());
        Assert.Null(ctx.PosteProuve());
        var (autre, passerAutre) = Requete(depot, horloge, "/api/caisse", enTetePoste: "CAISSE-2", user: Connecte("CAISSE-1"));
        Assert.False(await passerAutre());
        Assert.Equal(403, autre.Response.StatusCode);
    }

    [Fact]
    public async Task Mode_transition_poste_prouve_poste_annonce_accepte_et_nom_reserve_refuse()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, cle) = depot.Actif("CAISSE-1");

        var (prouve, passerProuve) = Requete(depot, horloge, "/api/caisse", Signer(cle, ligne.Id, "GET", "/api/caisse", horloge.Unix), enTetePoste: "CAISSE-1");
        Assert.True(await passerProuve());
        Assert.Equal("CAISSE-1", prouve.Request.Poste());

        var (_, passerLibre) = Requete(depot, horloge, "/api/caisse", enTetePoste: "COMPTOIR-3");
        Assert.True(await passerLibre());

        var (reserve, passerReserve) = Requete(depot, horloge, "/api/caisse", enTetePoste: "caisse-1");
        Assert.False(await passerReserve());
        Assert.Equal(403, reserve.Response.StatusCode);

        // Jeton lié à un nom réservé, requête sans preuve : refusé
        var (jetonReserve, passerJeton) = Requete(depot, horloge, "/api/caisse", user: Connecte("CAISSE-1"));
        Assert.False(await passerJeton());
        Assert.Equal(401, jetonReserve.Response.StatusCode);

        // Client sans poste (page mobile) : inchangé
        var (_, passerMobile) = Requete(depot, horloge, "/api/mobile/scan", user: Connecte());
        Assert.True(await passerMobile());
    }

    [Fact]
    public async Task Mode_exige_un_poste_sans_preuve_est_refuse_sauf_sur_les_routes_ouvertes()
    {
        var horloge = new Horloge();
        var depot = new Depot { Reglages = ReglagesPostes.ParDefaut with { Mode = PreuvePoste.ModeExige } };
        var (ctx, passer) = Requete(depot, horloge, "/api/caisse", enTetePoste: "COMPTOIR-3");
        Assert.False(await passer());
        Assert.Equal(403, ctx.Response.StatusCode);
        foreach (var ouverte in new[] { "/api/healthcheck", "/api/postes/etat", "/api/postes/enrolement", "/api/vigie/rapport" })
        {
            var (_, p) = Requete(depot, horloge, ouverte, enTetePoste: "COMPTOIR-3");
            Assert.True(await p(), ouverte);
        }
        // Jeton sans poste_id (ouvert avant l'enrôlement) : refusé en mode exige
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var (ancien, passerAncien) = Requete(depot, horloge, "/api/caisse", Signer(cle, ligne.Id, "GET", "/api/caisse", horloge.Unix), user: Connecte("CAISSE-1"));
        Assert.False(await passerAncien());
        Assert.Equal(401, ancien.Response.StatusCode);
    }

    [Fact]
    public async Task Le_jeton_d_un_poste_n_est_accepte_que_sur_ce_poste_prouve()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (l1, c1) = depot.Actif("CAISSE-1");
        var (l2, c2) = depot.Actif("CAISSE-2");
        var (_, ok) = Requete(depot, horloge, "/api/caisse", Signer(c1, l1.Id, "GET", "/api/caisse", horloge.Unix), user: Connecte("CAISSE-1", l1.Id));
        Assert.True(await ok());
        var (autre, ko) = Requete(depot, horloge, "/api/caisse", Signer(c2, l2.Id, "GET", "/api/caisse", horloge.Unix), user: Connecte("CAISSE-1", l1.Id));
        Assert.False(await ko());
        Assert.Equal(401, autre.Response.StatusCode);
        var (sansPreuve, ko2) = Requete(depot, horloge, "/api/caisse", user: Connecte("CAISSE-1", l1.Id));
        Assert.False(await ko2());
        Assert.Equal(401, sansPreuve.Response.StatusCode);
    }

    [Fact]
    public async Task Une_preuve_fausse_donne_401_et_l_heure_du_serveur_pour_l_horloge()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var (ctx, passer) = Requete(depot, horloge, "/api/caisse", Signer(cle, ligne.Id, "GET", "/api/caisse", horloge.Unix - 600));
        Assert.False(await passer());
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.True(ctx.Response.Headers.ContainsKey(PreuvePoste.EnTeteHeureServeur));
        // Route ouverte : jamais refusée, le refus est seulement lu
        var (etat, passerEtat) = Requete(depot, horloge, "/api/postes/etat", Signer(cle, ligne.Id, "GET", "/api/postes/etat", horloge.Unix - 600));
        Assert.True(await passerEtat());
        Assert.Equal(PreuvePoste.PreuveHorloge, PreuvePosteMiddleware.Refus(etat));
    }

    [Fact]
    public async Task Preuve_fausse_ou_inconnue_vaut_absence_de_preuve_en_transition_et_refus_si_exige()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, _) = depot.Actif("CAISSE-1");
        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);   // poste enrôlé sur une autre base : clé inconnue ici
        EnTetesPreuve Fausse(string chemin) => Signer(autre, ligne.Id, "GET", chemin, horloge.Unix);

        var (libre, passerLibre) = Requete(depot, horloge, "/api/caisse", Fausse("/api/caisse"), enTetePoste: "COMPTOIR-3");
        Assert.True(await passerLibre());
        Assert.Null(libre.PosteProuve());
        Assert.Equal("COMPTOIR-3", libre.Request.Poste());
        // Nom réservé annoncé avec une preuve fausse : refusé (D6)
        var (reserve, passerReserve) = Requete(depot, horloge, "/api/caisse", Fausse("/api/caisse"), enTetePoste: "CAISSE-1");
        Assert.False(await passerReserve());
        Assert.Equal(403, reserve.Response.StatusCode);

        depot.Reglages = depot.Reglages with { Mode = PreuvePoste.ModeExige };
        var (exige, passerExige) = Requete(depot, horloge, "/api/caisse", Fausse("/api/caisse"), enTetePoste: "COMPTOIR-3");
        Assert.False(await passerExige());
        Assert.Equal(401, exige.Response.StatusCode);
    }

    [Fact]
    public void Le_jeton_recoit_poste_id_seulement_avec_un_poste_prouve_du_meme_nom()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Jwt:Cle", new string('k', 80)), new("Jwt:Emetteur", "pharma")]).Build();
        var http = new DefaultHttpContext();
        http.Items[PorteeConnexion.ItemPosteProuve] = new PosteProuve(7, "CAISSE-1");
        var jetons = new JetonService(config, new HttpContextAccessor { HttpContext = http });
        var avec = new JsonWebTokenHandler().ReadJsonWebToken(jetons.Creer(5, "Vendeur", poste: "CAISSE-1").Jeton);
        Assert.Equal("7", avec.Claims.Single(c => c.Type == PorteeConnexion.RevendicationPosteId).Value);
        var autreNom = new JsonWebTokenHandler().ReadJsonWebToken(jetons.Creer(5, "Vendeur", poste: "CAISSE-2").Jeton);
        Assert.DoesNotContain(autreNom.Claims, c => c.Type == PorteeConnexion.RevendicationPosteId);
    }

    // ---- Enrôlement -------------------------------------------------------------------------------------------------------------------------

    private static (EnrolerPoste Enroler, GererPostesAutorises Gerer, Depot Depot, Horloge Horloge, Journal Journal, Alertes Alertes, Droits Droits) BancGestion()
    {
        var horloge = new Horloge();
        var depot = new Depot { Maintenant = () => horloge.MaintenantUtc };
        var registre = new RegistrePostesAutorises(depot, horloge);
        var journal = new Journal();
        var alertes = new Alertes();
        var droits = new Droits();
        return (new EnrolerPoste(depot, registre, journal, alertes, new AlertesPostes(horloge), horloge), new GererPostesAutorises(depot, registre, droits, journal, horloge, depot),
            depot, horloge, journal, alertes, droits);
    }

    private static EnrolementPosteRequete Demande(string code, ECDsa cle, long horodatage, string? ordinateur = "PC-CAISSE1")
    {
        var spki = cle.ExportSubjectPublicKeyInfo();
        return new EnrolementPosteRequete(code, Convert.ToBase64String(spki), ordinateur, horodatage, PreuvePoste.Signer(cle, PreuvePoste.ChaineEnrolement(code, horodatage, spki)));
    }

    [Fact]
    public async Task Le_code_enrole_le_poste_une_seule_fois()
    {
        var (enroler, gerer, depot, horloge, journal, _, _) = BancGestion();
        var code = await gerer.AutoriserAsync(1, "CAISSE-1");
        Assert.Equal(horloge.MaintenantUtc.AddMinutes(15), code.ExpireLe);
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var r = await enroler.ExecuterAsync(Demande(code.Code.ToLowerInvariant(), cle, horloge.Unix));
        Assert.Equal(new EnrolementPosteReponse(code.PosteId, "CAISSE-1"), r);
        Assert.Equal("actif", depot.Lignes.Single().Statut);
        Assert.Contains(journal.Entrees, e => e.Action == GererPostesAutorises.ActionEnrole && e.Par == 1);
        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var e2 = await Assert.ThrowsAsync<RefusPosteException>(() => enroler.ExecuterAsync(Demande(code.Code, autre, horloge.Unix)));
        Assert.Equal(PreuvePoste.CodeInvalide, e2.Code);
    }

    [Fact]
    public async Task Code_expire_ou_preuve_de_possession_fausse_sont_refuses()
    {
        var (enroler, gerer, _, horloge, _, _, _) = BancGestion();
        var code = await gerer.AutoriserAsync(1, "CAISSE-1");
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var autre = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        // Preuve signée par une autre clé que celle envoyée
        var d = Demande(code.Code, cle, horloge.Unix) with { ClePublique = Convert.ToBase64String(autre.ExportSubjectPublicKeyInfo()) };
        Assert.Equal(PreuvePoste.CleInvalide, (await Assert.ThrowsAsync<RefusPosteException>(() => enroler.ExecuterAsync(d))).Code);
        horloge.MaintenantUtc = horloge.MaintenantUtc.AddMinutes(16);
        Assert.Equal(PreuvePoste.CodeInvalide,
            (await Assert.ThrowsAsync<RefusPosteException>(() => enroler.ExecuterAsync(Demande(code.Code, cle, horloge.Unix)))).Code);
    }

    [Fact]
    public async Task Cinq_codes_faux_invalident_les_codes_en_attente_et_alertent_le_titulaire()
    {
        var (enroler, gerer, _, horloge, _, alertes, _) = BancGestion();
        var code = await gerer.AutoriserAsync(1, "CAISSE-1");
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        for (var i = 0; i < 5; i++)
            await Assert.ThrowsAsync<RefusPosteException>(() => enroler.ExecuterAsync(Demande("2222-2222", cle, horloge.Unix)));
        Assert.Single(alertes.Messages);
        var e = await Assert.ThrowsAsync<RefusPosteException>(() => enroler.ExecuterAsync(Demande(code.Code, cle, horloge.Unix)));
        Assert.Equal(PreuvePoste.CodeInvalide, e.Code);
    }

    // ---- Gestion par le titulaire -----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nom_pris_sans_la_casse_et_maximum_de_postes_sont_refuses()
    {
        var (_, gerer, depot, _, _, _, _) = BancGestion();
        await gerer.AutoriserAsync(1, "CAISSE-1");
        Assert.Equal(PreuvePoste.NomPostePris, (await Assert.ThrowsAsync<RefusPosteException>(() => gerer.AutoriserAsync(1, "caisse-1"))).Code);
        depot.Maximum = 1;
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.AutoriserAsync(1, "CAISSE-2"));
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.AutoriserAsync(1, "   "));
    }

    [Fact]
    public async Task Le_maximum_vient_de_l_offre_et_une_offre_plus_petite_ne_retire_aucun_poste()
    {
        var (_, gerer, depot, _, _, _, _) = BancGestion();
        foreach (var n in new[] { "CAISSE-1", "CAISSE-2", "CAISSE-3" }) await gerer.AutoriserAsync(1, n);

        // Offre ramenée à 2 postes (offre de base, script 172) : aucun poste retiré, aucun nouveau
        depot.Maximum = 2;
        Assert.Equal(3, depot.Lignes.Count(l => l.Statut != "revoque"));
        var refus = await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.AutoriserAsync(1, "CAISSE-4"));
        Assert.Equal(GererPostesAutorises.MessageMaximumAtteint(2), refus.Message);
        var (_, _, maximum, comptes) = await gerer.ReglagesAsync(1);
        Assert.Equal((2, 3), (maximum, comptes));

        // Le réglage du titulaire ne touche plus au maximum
        Assert.Null(await gerer.ModifierReglagesAsync(1, depot.Reglages with { DureeCodeMinutes = 30 }));
        depot.Maximum = 4;
        await gerer.AutoriserAsync(1, "CAISSE-4");
    }

    [Fact]
    public async Task Renommer_est_refuse_avec_une_caisse_ouverte()
    {
        var (_, gerer, depot, _, journal, _, _) = BancGestion();
        var (ligne, _) = depot.Actif("CAISSE-1");
        depot.Occupes.Add("CAISSE-1");
        Assert.Equal(PreuvePoste.PosteOccupe, (await Assert.ThrowsAsync<RefusPosteException>(() => gerer.RenommerAsync(1, ligne.Id, "CAISSE-9"))).Code);
        depot.Occupes.Clear();
        await gerer.RenommerAsync(1, ligne.Id, "CAISSE-9");
        Assert.Equal("CAISSE-9", ligne.Nom);
        Assert.Contains(journal.Entrees, e => e.Action == GererPostesAutorises.ActionRenomme);
    }

    [Fact]
    public async Task Remplacer_le_PC_retire_la_cle_et_donne_un_nouveau_code_et_revoquer_exige_un_motif()
    {
        var (_, gerer, depot, _, _, _, _) = BancGestion();
        var (ligne, _) = depot.Actif("CAISSE-1");
        var code = await gerer.RemplacerAsync(1, ligne.Id);
        Assert.Equal(("en_attente", 2), (ligne.Statut, ligne.Generation));
        Assert.Null(ligne.Cle);
        Assert.Equal(ligne.Id, code.PosteId);
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.RevoquerAsync(1, ligne.Id, "vol"));
        await gerer.RevoquerAsync(1, ligne.Id, "PC volé la nuit");
        Assert.Equal("revoque", ligne.Statut);
    }

    [Fact]
    public async Task Seul_le_titulaire_gere_les_postes()
    {
        var (_, gerer, _, _, _, _, droits) = BancGestion();
        droits.Titulaire = false;
        await Assert.ThrowsAsync<DroitRefuseException>(() => gerer.AutoriserAsync(1, "CAISSE-1"));
        await Assert.ThrowsAsync<DroitRefuseException>(() => gerer.ModifierReglagesAsync(1, ReglagesPostes.ParDefaut));
    }

    [Fact]
    public async Task Les_reglages_hors_bornes_sont_refuses()
    {
        var (_, gerer, depot, _, journal, _, _) = BancGestion();
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.ModifierReglagesAsync(1, ReglagesPostes.ParDefaut with { Mode = "ouvert" }));
        await Assert.ThrowsAsync<Pharma.Domain.Commun.RegleMetierException>(() => gerer.ModifierReglagesAsync(1, ReglagesPostes.ParDefaut with { DureeCodeMinutes = 61 }));
        await gerer.ModifierReglagesAsync(1, ReglagesPostes.ParDefaut with { Mode = PreuvePoste.ModeExige });
        Assert.Equal(PreuvePoste.ModeExige, depot.Reglages.Mode);
        Assert.Contains(journal.Entrees, e => e.Action == GererPostesAutorises.ActionReglages);
    }
}
