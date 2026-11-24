using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Scripts de mise en production scripts/mep (base neuve d'officine, mise à jour, serveur central) : garde-fous vérifiés
/// sur le contenu des scripts, sans jamais les exécuter (docs/scripts-base-de-donnees.md).
/// </summary>
public partial class ScriptsMepTests
{
    private static readonly string[] Scripts = ["creer_base_officine.ps1", "mettre_a_jour_base_officine.ps1", "creer_base_centrale.ps1"];

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Lire(params string[] chemin) => File.ReadAllText(Path.Combine([RacineDepot(), .. chemin]));

    private static string Mep(string nom) => Lire("scripts", "mep", nom);

    private static IEnumerable<string> TousLesFichiersMep() => Directory.GetFiles(Path.Combine(RacineDepot(), "scripts", "mep"), "*.ps1");

    /// <summary>Code sans l'aide en bloc (&lt;# … #&gt;) ni les lignes de commentaire.</summary>
    private static string Code(string texte) =>
        string.Join('\n', BlocAide().Replace(texte, "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [GeneratedRegex(@"<#.*?#>", RegexOptions.Singleline)]
    private static partial Regex BlocAide();

    [Fact]
    public void Les_trois_scripts_et_les_outils_existent_avec_une_aide_complete()
    {
        foreach (var nom in Scripts)
        {
            var texte = Mep(nom);
            Assert.StartsWith("<#", texte.TrimStart('\uFEFF'));
            foreach (var rubrique in new[] { ".SYNOPSIS", ".DESCRIPTION", ".PARAMETER Base", ".EXAMPLE" })
                Assert.True(texte.Contains(rubrique), $"{nom} : l'aide doit contenir {rubrique}.");
        }
        Assert.True(File.Exists(Path.Combine(RacineDepot(), "scripts", "mep", "outils_mep.ps1")));
    }

    [Fact]
    public void Aucun_mot_de_passe_en_parametre_ni_en_clair()
    {
        foreach (var fichier in TousLesFichiersMep())
        {
            var texte = File.ReadAllText(fichier);
            var nom = Path.GetFileName(fichier);
            // Aucun paramètre dont le nom évoque un mot de passe
            Assert.DoesNotMatch(@"(?im)^\s*(\[Parameter[^\]]*\]\s*)?\[(string|securestring)\]\s*\$\w*(MotDePasse|Password|Passwd|Mdp|Pwd|Secret)\w*\s*[,=)]?", Code(texte).Split("Invoke-Principal")[0]);
            // Aucune valeur de mot de passe écrite dans le script
            Assert.DoesNotMatch(@"PGPASSWORD\s*=\s*[""'][^""'$]", texte);
            Assert.DoesNotContain("pharma_dev", texte);
            Assert.DoesNotContain("PGPASSFILE=", texte);
            Assert.DoesNotMatch(@"(?i)Out-File|Set-Content|Add-Content|>\s*\S*\.(txt|pgpass|conf)", Code(texte));
        }
        var outils = Mep("outils_mep.ps1");
        Assert.Contains("Read-Host $Invite -AsSecureString", outils);
        Assert.Contains("$env:PGPASSWORD = $script:MotsDePasse[$Compte]", outils);   // PGPASSWORD seulement dans le processus
        Assert.Contains("\"-w\", \"-X\"", outils);                                    // jamais d'invite interactive de psql
    }

    [Fact]
    public void Jamais_de_suppression_de_base()
    {
        foreach (var fichier in TousLesFichiersMep())
        {
            var texte = File.ReadAllText(fichier);
            Assert.DoesNotMatch(@"(?i)DROP\s+DATABASE", texte);
            Assert.DoesNotMatch(@"(?i)dropdb", texte);
            Assert.DoesNotMatch(@"(?i)\bclean\.sql\b", Code(texte));
            Assert.DoesNotMatch(@"(?i)Remove-Item[^\n]*\.(dump|sql)", Code(texte));
        }
        // Une base existante est refusée
        Assert.Contains("existe déjà", Mep("creer_base_officine.ps1"));
        Assert.Contains("contient déjà des tables centrales", Mep("creer_base_centrale.ps1"));
    }

    [Fact]
    public void Le_script_101b_n_est_jamais_execute_par_les_scripts_mep()
    {
        foreach (var fichier in TousLesFichiersMep())
        {
            var texte = File.ReadAllText(fichier);
            Assert.DoesNotContain("101b_", texte);
            Assert.DoesNotContain("suppression_num_facture", texte);
            foreach (var ligne in Code(texte).Split('\n').Where(l => l.Contains("101b")))
                Assert.DoesNotMatch(@"Invoke-Psql|-f\b|Join-Path|FullName|Get-ChildItem", ligne);
        }
        Assert.Contains("101b", Mep("mettre_a_jour_base_officine.ps1"));   // documenté : « jamais lancé »
    }

    [Fact]
    public void Le_motif_de_selection_couvre_001_a_la_derniere_et_exclut_les_scripts_b()
    {
        var outils = Mep("outils_mep.ps1");
        Assert.Contains(@"-match '^\d{3}_'", outils);
        var motif = new Regex(@"^\d{3}_");

        foreach (var dossier in new[] { "migration", "cloud" })
        {
            var tous = Directory.GetFiles(Path.Combine(RacineDepot(), "database", dossier), "*.sql").Select(Path.GetFileName).OfType<string>().ToList();
            var choisis = tous.Where(n => motif.IsMatch(n)).Order(StringComparer.Ordinal).ToList();
            Assert.DoesNotContain(choisis, n => Regex.IsMatch(n, @"^\d{3}b"));
            Assert.Equal(tous.Where(n => Regex.IsMatch(n, @"^\d{3}[^_]")).Count(), tous.Count - choisis.Count);   // seuls les « b » sont exclus
            Assert.Equal("001", choisis[0][..3]);
            if (dossier == "migration")
            {
                Assert.Contains(choisis, n => n.StartsWith("100_"));       // au-delà de 099
                Assert.Contains(choisis, n => n.StartsWith("139_"));
                Assert.DoesNotContain(choisis, n => n.StartsWith("101b") || n.StartsWith("111b"));
                Assert.DoesNotContain(choisis, n => n.StartsWith("163b") || n.StartsWith("166b"));
                Assert.Contains(tous, n => n.StartsWith("101b_"));          // le seul script « b » de la suite existe bien et est exclu
                var init = Directory.GetFiles(Path.Combine(RacineDepot(), "database", "init"), "*.sql").Select(Path.GetFileName).OfType<string>().ToList();
                Assert.Contains(init, n => n.StartsWith("111b_"));          // scripts « b » de mise en production : dans database/init
                Assert.Contains(init, n => n.StartsWith("163b_"));
                Assert.Contains(init, n => n.StartsWith("166b_"));
            }
        }

        // Outil de développement : même motif, plus l'ancien « ^0\d{2}_ » qui s'arrêtait à 099
        var dev = Lire("scripts", "initialiser_base_locale.ps1");
        Assert.Contains(@"-match ""^\d{3}_""", dev);
        Assert.DoesNotContain(@"^0\d{2}_", dev);
        Assert.DoesNotContain(@"-Filter ""0*.sql""", dev);
    }

    [Fact]
    public void Le_script_033_est_hors_transaction()
    {
        // 033 contient CREATE INDEX CONCURRENTLY : interdit dans une transaction ; c'est le seul script concerné
        var scriptsIndexEnLigne = Directory.GetFiles(Path.Combine(RacineDepot(), "database", "migration"), "*.sql")
            .Where(f => Regex.IsMatch(File.ReadAllText(f), @"(?im)^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY"))
            .Select(Path.GetFileName).ToList();
        Assert.Equal(["033_index_performance.sql"], scriptsIndexEnLigne);

        foreach (var fichier in TousLesFichiersMep())
        {
            var code = Code(File.ReadAllText(fichier));
            Assert.DoesNotContain("--single-transaction", code);
            Assert.DoesNotMatch(@"""-1""", code);
            Assert.DoesNotMatch(@"\\i\s", code);
        }
        // Un appel psql par script, avec -f, jamais un fichier regroupé dans BEGIN … COMMIT
        var maj = Code(Mep("mettre_a_jour_base_officine.ps1"));
        Assert.Contains(@"""-f"", $s.FullName", maj);
        Assert.DoesNotMatch(@"(?i)BEGIN;\s*""\s*\+|\bCOMMIT;", maj);
    }

    [Fact]
    public void Chaque_script_accepte_WhatIf()
    {
        foreach (var nom in Scripts)
        {
            var texte = Mep(nom);
            Assert.Matches(@"\[CmdletBinding\([^)]*SupportsShouldProcess\s*=\s*\$true", texte);
            Assert.Contains("$PSCmdlet.ShouldProcess(", texte);
            Assert.Contains("-Simulation $WhatIfPreference", texte);
        }
    }

    [Fact]
    public void L_entete_d_init_est_controle_avant_toute_creation()
    {
        var outils = Mep("outils_mep.ps1");
        Assert.Contains("function Test-EnteteInit", outils);
        Assert.Contains(@"""001 \u00e0 """, outils);

        var neuve = Mep("creer_base_officine.ps1");
        var posControle = neuve.IndexOf("Test-EnteteInit -Fichier $schema", StringComparison.Ordinal);
        Assert.True(posControle > 0);
        Assert.Contains("Test-EnteteInit -Fichier $seed", neuve);
        Assert.True(posControle < neuve.IndexOf("\"-c\", \"CREATE DATABASE", StringComparison.Ordinal), "Le contrôle de l'en-tête doit précéder la création de la base.");
        Assert.True(posControle < neuve.IndexOf("Request-MotDePasse", StringComparison.Ordinal), "Le contrôle de l'en-tête ne doit demander aucun mot de passe.");

        // Les en-têtes réels du dépôt sont à jour (le test ScriptsInitTests le vérifie aussi côté SQL)
        var dernier = Directory.GetFiles(Path.Combine(RacineDepot(), "database", "migration"), "*.sql")
            .Select(f => Path.GetFileName(f)).Where(n => Regex.IsMatch(n, @"^\d{3}_")).Order(StringComparer.Ordinal).Last()[..3];
        Assert.Contains($"001 à {dernier}", Lire("database", "init", "schema.sql"));
        Assert.Contains($"001 à {dernier}", Lire("database", "init", "seed_referentiel.sql"));
    }

    [Fact]
    public void Arret_sur_erreur_partout_et_code_de_sortie_non_nul()
    {
        var outils = Mep("outils_mep.ps1");
        Assert.Contains("ON_ERROR_STOP=1", outils);
        Assert.Contains("exit 1", outils);
        foreach (var nom in Scripts)
        {
            var texte = Mep(nom);
            Assert.Contains("$ErrorActionPreference = \"Stop\"", texte);
            Assert.Contains("Invoke-Principal", texte);
        }
        // Chaque appel d'une commande SQL passe par Invoke-Psql (ON_ERROR_STOP) : aucun psql lancé directement
        foreach (var nom in Scripts)
            Assert.DoesNotMatch(@"&\s*\$(script:)?Psql\b", Code(Mep(nom)));
    }

    [Fact]
    public void Mise_a_jour_sauvegarde_controle_service_et_conflits_chifa_avant_101()
    {
        var maj = Mep("mettre_a_jour_base_officine.ps1");
        Assert.Contains("-Fc", maj);
        Assert.Contains("--list", maj);
        Assert.Contains("PharmaOfficine API", maj);
        Assert.Contains("ContinuerMalgreConflitsChifa", maj);
        var posDump = maj.IndexOf("\"-Fc\", \"-f\"", StringComparison.Ordinal);
        Assert.True(posDump > 0 && posDump < maj.IndexOf("Write-Etape \"Rejeu des scripts", StringComparison.Ordinal), "La sauvegarde précède le rejeu.");
        Assert.True(maj.IndexOf("Get-ConflitsChifa -Base", StringComparison.Ordinal) < maj.IndexOf("[void](Invoke-Psql -Compte $CompteSuperUtilisateur -Base $Base -Role $RoleProprietaire", StringComparison.Ordinal));
        Assert.Contains("SET ROLE", Mep("outils_mep.ps1"));
        Assert.Contains("[À VÉRIFIER]", maj);   // méthode SET ROLE non validée par un essai avec le superutilisateur
    }

    [Fact]
    public void Mise_a_jour_rejoue_toujours_depuis_001_sans_depart_automatique()
    {
        var maj = Mep("mettre_a_jour_base_officine.ps1");
        var code = Code(maj);
        // Départ à 101 supprimé : toute la suite se rejoue sur une base à jour (scripts rejouables, ScriptsRejouablesTests)
        Assert.DoesNotContain("$depart = 101", code);
        Assert.DoesNotContain("marqueur101", code);
        Assert.Contains("$depart = if ($ScriptDepart -gt 0) { $ScriptDepart } else { 1 }", code);
        // Le paramètre manuel reste, documenté comme réservé aux incidents
        Assert.Contains("[int]$ScriptDepart = 0", code);
        Assert.Contains("n'utiliser qu'en cas d'incident", maj);
    }

    [Fact]
    public void Le_central_passe_les_scripts_cloud_dans_l_ordre_et_les_tests_en_option()
    {
        var central = Mep("creer_base_centrale.ps1");
        Assert.Contains(@"database\cloud", central);
        Assert.Contains("[switch]$AvecTests", central);
        Assert.Contains("Suite des scripts cloud interrompue", central);   // contrôle de continuité 001 à NNN
        var cloud = Directory.GetFiles(Path.Combine(RacineDepot(), "database", "cloud"), "*.sql").Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToList();
        for (var i = 0; i < cloud.Count; i++) Assert.StartsWith($"{i + 1:000}_", cloud[i]);   // sans trou, sinon le script refuse
    }

    [Fact]
    public void La_documentation_est_reliee()
    {
        Assert.True(File.Exists(Path.Combine(RacineDepot(), "docs", "scripts-base-de-donnees.md")));
        Assert.Contains("scripts-base-de-donnees.md", Lire("docs", "mise-en-production.md"));
        Assert.Contains("scripts-base-de-donnees.md", Lire("docs", "base-de-donnees.md"));
        foreach (var nom in Scripts) Assert.Contains(nom, Lire("docs", "scripts-base-de-donnees.md"));
    }
}
