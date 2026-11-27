using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Pharma.Api.Securite;
using Pharma.Application.Postes;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Titulaire sur un PC non autorisé : Administration seulement (décision du titulaire du 2026-11-27). Intergiciel de preuve et contrôle du jeton :
/// jeton marqué « administration_seule » limité à la liste fermée des préfixes, partout ; PC non prouvé sans jeton marqué refusé comme avant ;
/// PC prouvé inchangé. Liste fermée : figée ici, sans préfixe de caisse, de vente, de stock ou de finances, et couvrant chaque appel des écrans
/// permis sur le poste (Pharma.Desktop, Services/AdministrationSeule.cs).
/// </summary>
public partial class PostesAutorisesTests
{
    private static ClaimsPrincipal TitulaireAdministrationSeule() => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(AccesAdministrationSeule.Revendication, AccesAdministrationSeule.Valeur)], "Bearer"));

    private static string CodeRefus(DefaultHttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var doc = JsonDocument.Parse(ctx.Response.Body);
        return doc.RootElement.GetProperty("code").GetString()!;
    }

    private static async Task<(bool Passe, DefaultHttpContext Ctx)> PasserAsync(Depot depot, Horloge horloge, string chemin, string? enTetePoste,
        ClaimsPrincipal? user, EnTetesPreuve? preuve = null, string methode = "GET")
    {
        var (ctx, passer) = Requete(depot, horloge, chemin, preuve, enTetePoste, user, methode);
        if (user is not null) ctx.Request.Headers.Authorization = "Bearer jeton";
        return (await passer(), ctx);
    }

    [Fact]
    public async Task Titulaire_sur_un_PC_non_prouve_l_Administration_passe_la_caisse_est_refusee()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        // Connexion sans jeton : laissée passer jusqu'à SeConnecter (titulaire seulement), marquée « PC non prouvé »
        var (connexion, ctxConnexion) = await PasserAsync(depot, horloge, "/api/auth/connexion", "COMPTOIR-3", null, methode: "POST");
        Assert.True(connexion);
        Assert.True(AccesAdministrationSeule.PcNonProuve(ctxConnexion));
        Assert.True(AccesAdministrationSeule.Demande(ctxConnexion));

        foreach (var admin in new[] { "/api/admin/postes", "/api/admin/postes/4/code", "/api/offre/activation", "/api/admin/utilisateurs", "/api/auth/permissions",
                     "/api/auth/mot-de-passe", "/api/admin/sauvegardes/journal" })
        {
            var (passe, _) = await PasserAsync(depot, horloge, admin, "COMPTOIR-3", TitulaireAdministrationSeule());
            Assert.True(passe, admin);
        }
        foreach (var autre in new[] { "/api/caisse/etat", "/api/caisse/ventes", "/api/stock", "/api/produits", "/api/tresorerie/comptes", "/api/financements/prets",
                     "/api/factures", "/api/achats/commandes", "/api/patients", "/api/admin/caisse", "/api/admin/journal", "/api/suggestions/preteur_pret",
                     "/api/auth/connexion-code", "/api/admin/reprise/sessions" })
        {
            var (passe, ctx) = await PasserAsync(depot, horloge, autre, "COMPTOIR-3", TitulaireAdministrationSeule());
            Assert.False(passe, autre);
            Assert.Equal(403, ctx.Response.StatusCode);   // 403 et non 401 : la session du poste reste ouverte
            Assert.Equal(AccesAdministrationSeule.Code, CodeRefus(ctx));
        }
    }

    [Fact]
    public async Task Le_jeton_marque_reste_limite_a_l_Administration_partout()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        // Rejoué sans l'en-tête X-Pharma-Poste (comme un client sans poste) : toujours limité
        var (mobileAdmin, _) = await PasserAsync(depot, horloge, "/api/admin/postes", null, TitulaireAdministrationSeule());
        Assert.True(mobileAdmin);
        var (mobileCaisse, ctxMobile) = await PasserAsync(depot, horloge, "/api/caisse/etat", null, TitulaireAdministrationSeule());
        Assert.False(mobileCaisse);
        Assert.Equal(403, ctxMobile.Response.StatusCode);
        // PC autorisé ensuite (poste prouvé) : le jeton marqué reste limité jusqu'à une nouvelle connexion
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var (prouveCaisse, ctxProuve) = await PasserAsync(depot, horloge, "/api/caisse/etat", "CAISSE-1", TitulaireAdministrationSeule(),
            Signer(cle, ligne.Id, "GET", "/api/caisse/etat", horloge.Unix, "Bearer jeton"));
        Assert.False(prouveCaisse);
        Assert.Equal(AccesAdministrationSeule.Code, CodeRefus(ctxProuve));
        // Routes ouvertes : jamais refusées
        var (etat, _) = await PasserAsync(depot, horloge, "/api/postes/etat", "COMPTOIR-3", TitulaireAdministrationSeule());
        Assert.True(etat);
    }

    [Fact]
    public async Task Un_employe_non_titulaire_sur_un_PC_non_prouve_est_refuse_comme_avant()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        // Jeton non marqué (ouvert ailleurs) présenté depuis un PC non prouvé : refus d'origine, même sur une route de l'Administration
        foreach (var chemin in new[] { "/api/admin/postes", "/api/caisse/etat" })
        {
            var (passe, ctx) = await PasserAsync(depot, horloge, chemin, "COMPTOIR-3", Connecte());
            Assert.False(passe, chemin);
            Assert.Equal(403, ctx.Response.StatusCode);
            Assert.Equal(PreuvePoste.PosteNonAutorise, CodeRefus(ctx));
        }
        // Sans jeton, hors de la connexion : refus d'origine
        var (anonyme, ctxAnonyme) = await PasserAsync(depot, horloge, "/api/admin/postes", "COMPTOIR-3", null);
        Assert.False(anonyme);
        Assert.Equal(PreuvePoste.PosteNonAutorise, CodeRefus(ctxAnonyme));
        // (la connexion elle-même refuse tout autre employé que le titulaire : SeConnecter, Pharma.Application.Tests.ConnexionPcNonAutoriseTests)
    }

    [Fact]
    public async Task Poste_revoque_le_titulaire_garde_l_Administration_les_autres_jetons_sont_refuses_comme_avant()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        ligne.Statut = "revoque";
        EnTetesPreuve Preuve(string chemin, string methode = "GET") => Signer(cle, ligne.Id, methode, chemin, horloge.Unix, "Bearer jeton");

        var (admin, _) = await PasserAsync(depot, horloge, "/api/admin/postes", "CAISSE-1", TitulaireAdministrationSeule(), Preuve("/api/admin/postes"));
        Assert.True(admin);
        var (caisse, ctxCaisse) = await PasserAsync(depot, horloge, "/api/caisse/etat", "CAISSE-1", TitulaireAdministrationSeule(), Preuve("/api/caisse/etat"));
        Assert.False(caisse);
        Assert.Equal(AccesAdministrationSeule.Code, CodeRefus(ctxCaisse));
        // Jeton ordinaire du poste révoqué : 401 « poste_revoque », comme avant
        var (ancien, ctxAncien) = await PasserAsync(depot, horloge, "/api/caisse/etat", "CAISSE-1", Connecte("CAISSE-1", ligne.Id), Preuve("/api/caisse/etat"));
        Assert.False(ancien);
        Assert.Equal(401, ctxAncien.Response.StatusCode);
        Assert.Equal(PreuvePoste.PosteRevoque, CodeRefus(ctxAncien));
        // Connexion depuis le poste révoqué : ouverte au titulaire seul
        var (connexion, _) = await PasserAsync(depot, horloge, "/api/auth/connexion", "CAISSE-1", null, Signer(cle, ligne.Id, "POST", "/api/auth/connexion", horloge.Unix), "POST");
        Assert.True(connexion);
    }

    [Fact]
    public async Task Horloge_decalee_reste_refusee_meme_sur_l_Administration()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var (passe, ctx) = await PasserAsync(depot, horloge, "/api/admin/postes", "CAISSE-1", TitulaireAdministrationSeule(),
            Signer(cle, ligne.Id, "GET", "/api/admin/postes", horloge.Unix - 600, "Bearer jeton"));
        Assert.False(passe);
        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.Equal(PreuvePoste.PreuveHorloge, CodeRefus(ctx));
    }

    [Fact]
    public async Task Sur_un_poste_prouve_rien_ne_change()
    {
        var horloge = new Horloge();
        var depot = new Depot();
        var (ligne, cle) = depot.Actif("CAISSE-1");
        var (ctx, passer) = Requete(depot, horloge, "/api/caisse/etat", Signer(cle, ligne.Id, "GET", "/api/caisse/etat", horloge.Unix), "CAISSE-1",
            Connecte("CAISSE-1", ligne.Id));
        Assert.True(await passer());
        Assert.False(AccesAdministrationSeule.PcNonProuve(ctx));
        Assert.False(AccesAdministrationSeule.Demande(ctx));
    }

    [Fact]
    public void Le_jeton_emis_sur_un_PC_non_prouve_est_marque_sans_poste()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Jwt:Cle", new string('k', 80)), new("Jwt:Emetteur", "pharma")]).Build();
        var lecteur = new JsonWebTokenHandler();
        var nonProuve = new DefaultHttpContext();
        nonProuve.Items[AccesAdministrationSeule.ItemPcNonProuve] = true;
        var marque = lecteur.ReadJsonWebToken(new JetonService(config, new HttpContextAccessor { HttpContext = nonProuve }).Creer(1, "Titulaire", poste: "COMPTOIR-3").Jeton);
        Assert.Equal(AccesAdministrationSeule.Valeur, marque.Claims.Single(c => c.Type == AccesAdministrationSeule.Revendication).Value);
        Assert.DoesNotContain(marque.Claims, c => c.Type is PorteeConnexion.RevendicationPoste or PorteeConnexion.RevendicationPosteId);
        // Un jeton marqué n'engendre que des jetons marqués (changement du mot de passe), même sur un poste prouvé
        var connecte = new DefaultHttpContext { User = TitulaireAdministrationSeule() };
        connecte.Items[PorteeConnexion.ItemPosteProuve] = new PosteProuve(7, "CAISSE-1");
        var suite = lecteur.ReadJsonWebToken(new JetonService(config, new HttpContextAccessor { HttpContext = connecte }).Creer(1, "Titulaire", poste: "CAISSE-1").Jeton);
        Assert.Contains(suite.Claims, c => c.Type == AccesAdministrationSeule.Revendication);
        // Poste prouvé, jeton ordinaire : aucune marque
        var prouve = new DefaultHttpContext();
        prouve.Items[PorteeConnexion.ItemPosteProuve] = new PosteProuve(7, "CAISSE-1");
        var ordinaire = lecteur.ReadJsonWebToken(new JetonService(config, new HttpContextAccessor { HttpContext = prouve }).Creer(5, "Vendeur", poste: "CAISSE-1").Jeton);
        Assert.DoesNotContain(ordinaire.Claims, c => c.Type == AccesAdministrationSeule.Revendication);
        Assert.Contains(ordinaire.Claims, c => c.Type == PorteeConnexion.RevendicationPosteId);
    }

    // ---- Liste fermée --------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void La_liste_des_prefixes_est_fermee()
    {
        Assert.Equal(
        [
            "/api/auth/connexion", "/api/auth/mot-de-passe", "/api/auth/permissions", "/api/auth/affichage",
            "/api/admin/postes", "/api/offre", "/api/admin/pharmacie", "/api/pharmacie", "/api/admin/organismes", "/api/medecins", "/api/wilayas",
            "/api/suggestions/specialite_medecin",
            "/api/admin/utilisateurs", "/api/admin/droits", "/api/admin/notifications", "/api/parametres/formulaire-sms",
            "/api/admin/sauvegardes", "/api/admin/second-serveur", "/api/admin/synchronisation", "/api/reglages-mutualises"
        ], AccesAdministrationSeule.Prefixes);
        Assert.Equal(["/api/auth/connexion"], AccesAdministrationSeule.RoutesAnonymes);
        // Ni caisse, ni vente, ni stock, ni achats, ni patients, ni finances, ni réglages de caisse ou de contrôle
        string[] interdits = ["/api/caisse", "/api/ventes", "/api/stock", "/api/produits", "/api/catalogue", "/api/lots", "/api/inventaires", "/api/inventaire-complet",
            "/api/achats", "/api/patients", "/api/chifa", "/api/factures", "/api/tresorerie", "/api/financements", "/api/suivi-dettes", "/api/pilotage",
            "/api/surveillance", "/api/psychotropes", "/api/tracabilite", "/api/admin/caisse", "/api/admin/journal", "/api/admin/seuils-stock",
            "/api/admin/produits", "/api/admin/reprise", "/api/super-admin", "/api/auth/connexion-code", "/api/auth/code-comptoir"];
        foreach (var p in AccesAdministrationSeule.Prefixes)
        {
            Assert.StartsWith("/api/", p);
            Assert.DoesNotContain(interdits, i => p == i || p.StartsWith(i + "/", StringComparison.Ordinal) || i.StartsWith(p + "/", StringComparison.Ordinal));
        }
        // Segments entiers
        Assert.False(AccesAdministrationSeule.RouteAutorisee("/api/offres"));
        Assert.False(AccesAdministrationSeule.RouteAutorisee("/api/auth/connexion-code"));
        // Suggestions : la spécialité d'un médecin seulement (les autres champs touchent la caisse, les achats ou les finances)
        Assert.Equal(["/api/suggestions/specialite_medecin"], AccesAdministrationSeule.Prefixes.Where(p => p.StartsWith("/api/suggestions", StringComparison.Ordinal)));
        Assert.False(AccesAdministrationSeule.RouteAutorisee("/api/suggestions/preteur_pret"));
        Assert.True(AccesAdministrationSeule.RouteAutorisee("/api/admin/postes/12/revoquer"));
    }

    private static readonly string[] EcransAttendus =
    [
        "admin/droits", "admin/notifications", "admin/imprimantes", "admin/sauvegardes", "admin/second-serveur", "admin/synchronisation",
        "admin/pharmacie", "admin/postes", "admin/offre"
    ];

    [Fact]
    public void Chaque_appel_des_ecrans_permis_sur_un_PC_non_autorise_est_couvert_par_la_liste()
    {
        var desktop = Path.Combine(BaseJetableM4.RacineDepot(), "src", "Pharma.Desktop");
        var source = File.ReadAllText(Path.Combine(desktop, "Services", "AdministrationSeule.cs"));
        var bloc = Regex.Match(source, @"Ecrans\s*=\s*\[(?<l>[^\]]*)\]").Groups["l"].Value;
        var ecrans = Regex.Matches(bloc, "\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(EcransAttendus, ecrans);

        // Méthodes de l'ApiClient → adresses appelées
        var api = File.ReadAllText(Path.Combine(desktop, "Services", "ApiClient.cs"));
        var declarations = Regex.Matches(api, @"public\s+(?:async\s+)?[^\n=;]*?\s(\w+)\s*\(").ToList();
        var methodes = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        for (var i = 0; i < declarations.Count; i++)
        {
            var fin = i + 1 < declarations.Count ? declarations[i + 1].Index : api.Length;
            var corps = api[declarations[i].Index..fin];
            var urls = Regex.Matches(corps, "\\$?\"/?(api/[^\"]*)\"").Select(m => "/" + m.Groups[1].Value.Split('?')[0]);
            if (!methodes.TryGetValue(declarations[i].Groups[1].Value, out var set)) methodes[declarations[i].Groups[1].Value] = set = [];
            set.UnionWith(urls);
        }

        var composants = Path.Combine(desktop, "Composants");
        var fichiers = Directory.GetFiles(composants, "*.razor", SearchOption.AllDirectories)
            .GroupBy(f => Path.GetFileNameWithoutExtension(f)).ToDictionary(g => g.Key, g => g.First());
        var pages = Directory.GetFiles(Path.Combine(composants, "Pages"), "Admin*.razor")
            .Select(f => (Fichier: f, Route: Regex.Match(File.ReadAllText(f), "@page \"/([^\"]+)\"").Groups[1].Value))
            .Where(p => ecrans.Contains(p.Route)).ToList();
        Assert.Equal(ecrans.Length, pages.Count);

        var ecarts = new List<string>();
        foreach (var (page, route) in pages)
        {
            var vus = new HashSet<string>();
            var file = new Stack<string>([page]);
            var adresses = new HashSet<string>();
            var champs = new HashSet<string>();
            while (file.Count > 0)
            {
                var f = file.Pop();
                if (!vus.Add(f)) continue;
                var texte = File.ReadAllText(f);
                foreach (Match m in Regex.Matches(texte, @"\bApi\.(\w+)\s*\("))
                    adresses.UnionWith(methodes.GetValueOrDefault(m.Groups[1].Value) ?? []);
                foreach (Match m in Regex.Matches(texte, "<ValeursSaisies[^>]*Champ=\"(\\w+)\"")) champs.Add(m.Groups[1].Value);
                foreach (Match m in Regex.Matches(texte, @"<(\w+)[\s/>]"))
                    if (fichiers.TryGetValue(m.Groups[1].Value, out var c) && !m.Groups[1].Value.StartsWith("Admin", StringComparison.Ordinal)) file.Push(c);
            }
            foreach (var brute in adresses)
            {
                // Partie fixe de l'adresse (avant le premier paramètre) ; suggestions : adresse réelle, par champ du formulaire
                var fixe = brute.Split('{')[0];
                var reelles = fixe == "/api/suggestions/" ? champs.Select(c => "/api/suggestions/" + c) : [fixe];
                foreach (var a in reelles)
                    if (!AccesAdministrationSeule.RouteAutorisee(a) && !PreuvePosteMiddleware.RouteOuverte(a)) ecarts.Add($"{route} : {a}");
            }
        }
        Assert.True(ecarts.Count == 0, "Appels non couverts par AccesAdministrationSeule.Prefixes : " + string.Join(", ", ecarts.Distinct()));
    }
}
