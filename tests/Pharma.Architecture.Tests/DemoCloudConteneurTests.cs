using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Démo cloud, étape 1 (docs/demo-cloud.md) : image de l'API (src/Pharma.Api/Dockerfile), contexte de construction
/// (.dockerignore), docker-compose local et script d'init de la base (deploy/demo). Garde-fous vérifiés sur le contenu
/// des fichiers, sans Docker : aucun conteneur n'est construit ni lancé par ces tests.
/// </summary>
public partial class DemoCloudConteneurTests
{
    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Chemin(params string[] parties) => Path.Combine([RacineDepot(), .. parties]);

    private static string Lire(params string[] parties) => File.ReadAllText(Chemin(parties));

    private static string Dockerfile => Lire("src", "Pharma.Api", "Dockerfile");
    private static string Compose => Lire("deploy", "demo", "docker-compose.yml");
    private static string CheminInit => Chemin("deploy", "demo", "init", "10_base_officine.sh");

    /// <summary>Lignes sans les commentaires (# en début de ligne).</summary>
    private static IEnumerable<string> Instructions(string texte) =>
        texte.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith('#'));

    private static List<string> LignesIgnorees() =>
        Instructions(Lire(".dockerignore")).Select(l => l.Trim()).ToList();

    // ---- Dockerfile ----

    [Fact]
    public void Dockerfile_en_deux_etapes_sdk_puis_aspnet_avec_ICU()
    {
        var from = Instructions(Dockerfile).Where(l => l.StartsWith("FROM ", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, from.Count);
        Assert.Matches(@"^FROM mcr\.microsoft\.com/dotnet/sdk:\S+ AS build$", from[0]);
        Assert.Matches(@"^FROM mcr\.microsoft\.com/dotnet/aspnet:\S+ AS final$", from[1]);
        Assert.Contains("COPY --from=build", Dockerfile);
        // Culture fr-FR : ni image sans ICU (chiseled, Alpine), ni mode invariant
        Assert.DoesNotMatch(@"(?i)chiseled|alpine|extra", string.Join('\n', from));
        Assert.DoesNotContain("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true", Dockerfile);
        Assert.DoesNotContain("InvariantGlobalization", Dockerfile);
    }

    [Fact]
    public void Dockerfile_restaure_avant_de_copier_les_sources_et_ignore_le_poste_et_les_tests()
    {
        var texte = string.Join('\n', Instructions(Dockerfile));
        var restore = texte.IndexOf("dotnet restore", StringComparison.Ordinal);
        Assert.True(restore > 0);
        foreach (var fichier in new[] { "NuGet.Config", "global.json", "Directory.Build.props", "Directory.Packages.props",
                     "src/Pharma.Api/Pharma.Api.csproj", "src/Pharma.Application/Pharma.Application.csproj",
                     "src/Pharma.Infrastructure/Pharma.Infrastructure.csproj", "src/Pharma.Domain/Pharma.Domain.csproj",
                     "src/Pharma.Shared/Pharma.Shared.csproj" })
        {
            var i = texte.IndexOf(fichier, StringComparison.Ordinal);
            Assert.True(i >= 0 && i < restore, $"{fichier} doit être copié avant la restauration.");
        }
        // Sources copiées après la restauration ; scripts livrés avec l'API (Pharma.Api.csproj)
        Assert.True(texte.IndexOf("COPY src/Pharma.Api/ ", StringComparison.Ordinal) > restore);
        Assert.Contains("COPY database/migration/", texte);
        Assert.Matches(@"dotnet publish src/Pharma\.Api/Pharma\.Api\.csproj -c Release", texte);
        Assert.DoesNotContain("Pharma.Desktop", texte);
        Assert.DoesNotContain("tests/", texte);
        Assert.DoesNotMatch(@"(?m)^COPY \. ", texte);
    }

    [Fact]
    public void Dockerfile_utilisateur_non_root_et_dossier_de_donnees_attribue()
    {
        var users = Instructions(Dockerfile).Where(l => l.StartsWith("USER ", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(users);
        Assert.Equal("USER app", users[^1].Trim());
        // Le dernier USER vient après l'installation des paquets (rien ne tourne en root au lancement)
        Assert.True(Dockerfile.LastIndexOf("USER app", StringComparison.Ordinal) > Dockerfile.LastIndexOf("apt-get install", StringComparison.Ordinal));
        Assert.Contains("install -d -o app -g app", Dockerfile);
        Assert.Contains("/var/lib/pharmaofficine", Dockerfile);
    }

    [Fact]
    public void Dockerfile_reglages_de_l_image()
    {
        foreach (var attendu in new[] { "TZ=Africa/Algiers", "ASPNETCORE_URLS=http://+:8080", "Urls=http://+:8080",
                     "Donnees__Racine=/var/lib/pharmaofficine", "Sauvegardes__PgDump=/usr/lib/postgresql/", "EXPOSE 8080", "tzdata",
                     "postgresql-client-${PG_MAJEURE}", "apt.postgresql.org", "ENTRYPOINT [\"dotnet\", \"Pharma.Api.dll\"]" })
            Assert.Contains(attendu, Dockerfile);
        // Client PostgreSQL de version >= serveur (postgres:18 du compose)
        var pg = Regex.Match(Dockerfile, @"ARG PG_MAJEURE=(\d+)");
        Assert.True(pg.Success);
        var serveur = Regex.Match(Compose, @"image:\s*postgres:(\d+)");
        Assert.True(serveur.Success);
        Assert.True(int.Parse(pg.Groups[1].Value) >= int.Parse(serveur.Groups[1].Value), "Client PostgreSQL plus ancien que le serveur.");
    }

    [Fact]
    public void Dockerfile_HEALTHCHECK_sur_la_route_de_sante()
    {
        Assert.Matches(@"(?s)HEALTHCHECK [^\n]*\\\s*\n\s*CMD curl -fsS http://localhost:8080/api/healthcheck", Dockerfile);
        // La route existe et reste anonyme
        Assert.Contains("app.MapGet(\"/api/healthcheck\"", Lire("src", "Pharma.Api", "Endpoints", "PharmaEndpoints.cs"));
    }

    [Fact]
    public void Dockerfile_sans_secret_ni_reglages_du_poste_de_developpement()
    {
        var instructions = Instructions(Dockerfile).ToList();
        Assert.DoesNotContain(instructions, l => l.StartsWith("COPY", StringComparison.Ordinal) && l.Contains("appsettings.Development", StringComparison.Ordinal));
        Assert.Contains("rm -f /app/publish/appsettings.Development.json", Dockerfile);
        Assert.DoesNotMatch(@"(?i)Password\s*=", Dockerfile);
        Assert.DoesNotMatch(@"(?i)Jwt__Cle\s*=|Jwt:Cle\s*=", Dockerfile);
        Assert.DoesNotContain("A-REMPLACER", Dockerfile);
        Assert.DoesNotMatch(@"(?m)^\s*(ENV|ARG)\s+\S*(MDP|PASSWORD|SECRET|CLE)\S*", Dockerfile.ToUpperInvariant());
    }

    // ---- .dockerignore ----

    [Fact]
    public void Dockerignore_exclut_sorties_poste_tests_et_secrets()
    {
        var lignes = LignesIgnorees();
        foreach (var attendu in new[] { "**/bin/", "**/obj/", ".git/", ".vs/", "tests/", "src/Pharma.Desktop/", "demo/", "docs/",
                     "**/*.user", "**/.env", "**/appsettings.Development.json", "**/appsettings.Https.json", "**/*.dump", "**/captures/" })
            Assert.Contains(attendu, lignes);
        // Les scripts livrés avec l'API restent dans le contexte
        Assert.DoesNotContain("database/", lignes);
        Assert.DoesNotContain("database/migration/", lignes);
    }

    // ---- docker-compose ----

    [Fact]
    public void Compose_sans_mot_de_passe_ni_cle_en_clair()
    {
        var texte = string.Join('\n', Instructions(Compose));
        Assert.DoesNotMatch(@"(?m)^\s*POSTGRES_PASSWORD\s*:", texte);
        Assert.Contains("POSTGRES_PASSWORD_FILE: /run/secrets/postgres_mdp", texte);
        // Chaque « Password= » et la clé JWT ne valent qu'une variable de deploy/demo/.env
        foreach (Match m in Regex.Matches(texte, @"(?i)Password=([^;""\s]*)"))
            Assert.StartsWith("${", m.Groups[1].Value);
        Assert.Matches(@"(?m)^\s*Jwt__Cle:\s*""\$\{JWT_CLE:\?", texte);
        Assert.DoesNotContain("A-REMPLACER", texte);
        Assert.DoesNotContain("pharma_dev", texte);
        // Secrets tirés de l'environnement (.env), jamais d'un fichier du dépôt
        Assert.Matches(@"(?ms)^secrets:\s*\n\s+postgres_mdp:\s*\n\s+environment:\s*POSTGRES_MDP\s*\n\s+pharma_app_mdp:\s*\n\s+environment:\s*PHARMA_APP_MDP", texte);
    }

    [Fact]
    public void Compose_api_attend_la_base_saine_et_garde_ses_donnees_sur_un_volume()
    {
        var texte = string.Join('\n', Instructions(Compose));
        Assert.Matches(@"(?s)depends_on:\s*\n\s+db:\s*\n\s+condition:\s*service_healthy", texte);
        Assert.Contains("- api-donnees:/var/lib/pharmaofficine", texte);
        Assert.Contains("- db-donnees:/var/lib/postgresql", texte);
        Assert.Matches(@"(?m)^volumes:\s*\n\s+db-donnees:\s*\n\s+api-donnees:", texte);
        Assert.Contains("dockerfile: src/Pharma.Api/Dockerfile", texte);
        Assert.Contains("context: ../..", texte);
        Assert.Matches(@"""127\.0\.0\.1:\$\{PORT_API_DEMO:-5080\}:8080""", texte);
        Assert.Contains("AutoriserHttpReseau: \"true\"", texte);
        Assert.Contains("LOCAL SEULEMENT", Compose);
        Assert.Contains("Database=pharmacie_demo", texte);
        // Base prête seulement après l'init (TCP), healthcheck de l'API sur la route de santé
        Assert.Contains("pg_isready -h 127.0.0.1", texte);
        Assert.Contains("http://localhost:8080/api/healthcheck", texte);
        Assert.Contains("- ./init:/docker-entrypoint-initdb.d:ro", texte);
        // Aucun service central à cette étape
        Assert.DoesNotMatch(@"(?im)^\s{2}\w*(central|cloud)\w*:\s*$", texte);
        var debut = texte.IndexOf("services:", StringComparison.Ordinal);
        var services = texte[debut..texte.IndexOf("\nsecrets:", debut, StringComparison.Ordinal)];
        // Étape 2 : service « reinit » de remise à zéro de la base de démonstration (ModeDemoTests)
        Assert.Equal(["db", "api", "reinit"], Regex.Matches(services, @"(?m)^  ([a-z][\w-]*):[ \t]*$").Select(m => m.Groups[1].Value));
    }

    // ---- .env.example et .gitignore ----

    [Fact]
    public void Env_example_ne_contient_que_des_valeurs_factices()
    {
        var valeurs = Instructions(Lire("deploy", "demo", ".env.example"))
            .Select(l => l.Split('=', 2)).ToDictionary(p => p[0].Trim(), p => p.Length > 1 ? p[1].Trim() : "");
        foreach (var cle in new[] { "POSTGRES_MDP", "PHARMA_APP_MDP", "JWT_CLE" })
        {
            Assert.True(valeurs.ContainsKey(cle), $"{cle} absente de .env.example");
            Assert.StartsWith("A-REMPLACER", valeurs[cle]);
        }
        foreach (var (cle, valeur) in valeurs.Where(v => Regex.IsMatch(v.Key, "MDP|PASSWORD|CLE|SECRET|KEY")))
            Assert.StartsWith("A-REMPLACER", valeur);
        // Clé JWT d'exemple : refusée par l'API au démarrage (JetonService)
        Assert.StartsWith(Pharma.Api.Securite.JetonService.PrefixeCleExemple, valeurs["JWT_CLE"]);
    }

    [Fact]
    public void Env_ignore_par_git()
    {
        var lignes = Lire(".gitignore").Split('\n').Select(l => l.Trim()).ToList();
        Assert.Contains(".env", lignes);
        Assert.DoesNotContain("*.env*", lignes);
        Assert.DoesNotContain(".env*", lignes);
    }

    // ---- Script d'init de la base ----

    [Fact]
    public void Script_d_init_en_LF_avec_arret_sur_erreur()
    {
        var octets = File.ReadAllBytes(CheminInit);
        Assert.DoesNotContain((byte)'\r', octets);
        Assert.False(octets.Length >= 3 && octets[0] == 0xEF && octets[1] == 0xBB && octets[2] == 0xBF, "Pas de BOM dans un script bash.");
        var texte = File.ReadAllText(CheminInit);
        Assert.StartsWith("#!/usr/bin/env bash\n", texte);
        Assert.Matches(@"(?m)^set -euo pipefail$", texte);
        Assert.Contains("*.sh text eol=lf", Lire("deploy", "demo", "init", ".gitattributes"));
    }

    [Fact]
    public void Script_d_init_suit_l_ordre_des_bases_de_developpement()
    {
        // Choix A du titulaire (2026-11-20) : comme scripts/initialiser_base_locale.ps1 (migrations NNN_*.sql dans l'ordre, sans
        // les scripts « b », puis toutes les données fictives), car la voie de la mise en production (init + référentiel) crée
        // déjà le compte « titulaire » et 900_donnees_demo.sql ne chargeait alors rien
        var init = string.Join('\n', Instructions(File.ReadAllText(CheminInit)));
        var ordre = new[] { "CREATE ROLE pharma_app LOGIN CREATEDB", "CREATE DATABASE :\"base\" OWNER pharma_app ENCODING 'UTF8' TEMPLATE template0",
            "for fichier in \"${MIGRATIONS[@]}\"", "for fichier in \"${DONNEES[@]}\"" };
        var positions = ordre.Select(o => init.IndexOf(o, StringComparison.Ordinal)).ToList();
        for (var i = 0; i < ordre.Length; i++) Assert.True(positions[i] >= 0, $"Absent du script d'init : {ordre[i]}");
        for (var i = 1; i < ordre.Length; i++) Assert.True(positions[i - 1] < positions[i], $"Ordre : {ordre[i - 1]} avant {ordre[i]}");
        // Sélection : NNN_*.sql seulement (101b et 111b exclus), triés ; toutes les données de database/data
        Assert.Contains("-regex '.*/[0-9]{3}_[^/]*[.]sql' | sort", init);
        Assert.Contains("find \"$SCRIPTS/data\" -maxdepth 1 -type f -name '*.sql' | sort", init);
        Assert.DoesNotContain("init/schema.sql", init);
        Assert.DoesNotContain("seed_referentiel.sql", init);

        // Même sélection dans le script des bases de développement
        var local = Lire("scripts", "initialiser_base_locale.ps1");
        Assert.Contains(@"-match ""^\d{3}_""", local);
        Assert.Contains(@"Get-ChildItem (Join-Path $racine ""database\data"") -Filter ""*.sql"" | Sort-Object Name", local);
        // Les numéros restent sur trois chiffres (l'ordre alphabétique du script bash est alors l'ordre des numéros), sans doublon
        var numeros = Directory.GetFiles(Chemin("database", "migration"), "*.sql").Select(Path.GetFileName)
            .Where(n => System.Text.RegularExpressions.Regex.IsMatch(n!, @"^\d{3}_")).Select(n => n![..3]).ToList();
        Assert.NotEmpty(numeros);
        Assert.Equal(numeros.Count, numeros.Distinct().Count());
    }

    [Fact]
    public void Script_d_init_compte_applicatif_marqueur_et_controle_des_comptes_de_demonstration()
    {
        var texte = File.ReadAllText(CheminInit);
        Assert.Contains("export PGOPTIONS=\"-c client_min_messages=warning -c pharma.reglage_central=on\"", texte);
        Assert.Contains("pharma.reglage_central=on", Lire("scripts", "initialiser_base_locale.ps1"));
        Assert.Matches(@"psql_app\(\) \{ psql -X -q -v ON_ERROR_STOP=1 --username ""\$ROLE_APP""", texte);
        Assert.Contains("ROLE_APP=\"pharma_app\"", texte);
        // Les données fictives doivent être chargées (BL-DEMO-1) et les trois comptes présents, sinon l'init échoue
        var code = string.Join('\n', Instructions(texte));
        Assert.Contains("numero_bl = 'BL-DEMO-1'", code);
        Assert.Contains("identifiant IN ('titulaire', 'assistant', 'vendeur')", code);
        Assert.DoesNotContain("111b", code);
        Assert.DoesNotContain("NOMENCLATURE", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Script_d_init_sans_mot_de_passe_en_clair_ni_sur_la_ligne_de_commande()
    {
        var texte = File.ReadAllText(CheminInit);
        Assert.DoesNotContain("pharma_dev", texte);
        Assert.DoesNotContain("A-REMPLACER-", texte);
        Assert.DoesNotMatch(@"PASSWORD\s+'[^']", texte);
        Assert.DoesNotMatch(@"PGPASSWORD=[""']?[A-Za-z0-9]", texte);
        // Mot de passe lu dans le fichier du secret, transmis à psql par son environnement (\getenv), jamais par -v ni -c
        Assert.Contains("lire_secret \"${PHARMA_APP_MDP_FILE:-}\"", texte);
        Assert.Contains("\\getenv mdp MDP_APP_INIT", texte);
        Assert.Contains("PASSWORD :'mdp'", texte);
        Assert.DoesNotMatch(@"-v\s+mdp=", texte);
        Assert.DoesNotMatch(@"(?m)^[^#]*(>|>>)\s*[""$/\w]", Regex.Replace(texte, @">&2", ""));
    }
}
