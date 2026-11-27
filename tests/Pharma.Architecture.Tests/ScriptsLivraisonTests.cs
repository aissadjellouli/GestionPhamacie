using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lot P1 bis de la protection du code (2026-11-27) : la livraison porte son propre dossier scripts\, limité aux scripts utiles en
/// officine (liste fermée de scripts\mep\creer_livraison.ps1), et leurs commentaires sont retirés par scripts\mep\nettoyer_livraison.ps1
/// sans que leurs instructions changent.
/// </summary>
public class ScriptsLivraisonTests
{
    /// <summary>Fichiers de scripts\ qui ne partent jamais en officine : PC des sources, développement, paquet d'essai, serveur central.</summary>
    private static readonly string[] NonLivres =
    [
        @"creer_paquet_essai.ps1",
        @"initialiser_base_locale.ps1",
        @"dev\essai_cloud_officine.ps1",
        @"dev\preparer_central_vs.ps1",
        @"mep\creer_base_centrale.ps1",
        @"mep\creer_livraison.ps1",
        @"mep\nettoyer_livraison.ps1",
        @"mep\obfusquer_livraison.ps1",
        @"mep\NettoyageSql.cs",
        @"paquet\demarrer.cmd",
        @"paquet\installer.ps1",
        @"paquet\LISEZMOI.txt",
    ];

    private static string DossierScripts => Path.Combine(BaseJetableM4.RacineDepot(), "scripts");

    private static List<string> Livres()
    {
        var texte = File.ReadAllText(Path.Combine(DossierScripts, "mep", "creer_livraison.ps1"));
        var liste = Regex.Match(texte, @"\$scriptsLivres = @\((?<corps>[^)]*)\)");
        Assert.True(liste.Success, "Liste $scriptsLivres introuvable dans creer_livraison.ps1.");
        return Regex.Matches(liste.Groups["corps"].Value, "\"(?<f>[^\"]+)\"").Select(m => m.Groups["f"].Value).ToList();
    }

    [Fact]
    public void Chaque_fichier_de_scripts_est_classe_livre_ou_non_livre()
    {
        var livres = Livres();
        var presents = Directory.GetFiles(DossierScripts, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(DossierScripts, f).Replace('/', '\\')).ToList();

        Assert.Equal(livres.Count, livres.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Empty(livres.Intersect(NonLivres, StringComparer.OrdinalIgnoreCase));
        var absents = livres.Concat(NonLivres).Except(presents, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(absents.Count == 0, "Classés mais absents de scripts\\ : " + string.Join(", ", absents));
        var nonClasses = presents.Except(livres.Concat(NonLivres), StringComparer.OrdinalIgnoreCase).ToList();
        Assert.True(nonClasses.Count == 0,
            "Fichiers de scripts\\ à classer (livré : $scriptsLivres de creer_livraison.ps1 ; sinon : NonLivres de ce test) : " + string.Join(", ", nonClasses));
    }

    [Fact]
    public void Les_scripts_livres_n_appellent_aucun_script_non_livre()
    {
        var nomsNonLivres = NonLivres.Where(f => f.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)).Select(f => Path.GetFileName(f)).ToList();
        var ecarts = new List<string>();
        foreach (var s in Livres())
        {
            // Lignes d'instructions seulement : l'aide et les commentaires peuvent citer un script du PC des sources
            var instructions = File.ReadAllLines(Path.Combine(DossierScripts, s))
                .Where(l => Regex.IsMatch(l, @"\$PSScriptRoot|^\s*[.&]\s"));
            ecarts.AddRange(from l in instructions from n in nomsNonLivres where l.Contains(n, StringComparison.OrdinalIgnoreCase) select $"{s} appelle {n}");
        }
        Assert.True(ecarts.Count == 0, string.Join("\n", ecarts));
    }

    [Fact]
    public void Le_nettoyage_retire_les_commentaires_des_scripts_livres_sans_changer_leurs_instructions()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(powershell)) return;

        var copie = Path.Combine(Path.GetTempPath(), $"pharma_scripts_livres_{Guid.NewGuid():N}");
        try
        {
            var livres = Livres();
            foreach (var s in livres)
            {
                var cible = Path.Combine(copie, "scripts", s);
                Directory.CreateDirectory(Path.GetDirectoryName(cible)!);
                File.Copy(Path.Combine(DossierScripts, s), cible);
            }

            // Le script contrôle lui-même : analyse sans erreur, instructions identiques avant et après, second nettoyage sans effet
            var (code, sortie) = Lancer(powershell, Path.Combine(DossierScripts, "mep", "nettoyer_livraison.ps1"), $"-Dossier \"{copie}\"");
            Assert.True(code == 0, sortie);
            Assert.Contains($"Scripts PowerShell sans commentaires : {livres.Count}", sortie);

            foreach (var s in livres)
            {
                var avant = File.ReadAllBytes(Path.Combine(DossierScripts, s));
                var apres = File.ReadAllBytes(Path.Combine(copie, "scripts", s));
                Assert.True(apres.Length <= avant.Length, $"{s} a grossi au nettoyage.");
                Assert.True(Signature(avant) == Signature(apres), $"{s} : signature UTF-8 perdue ou ajoutée (Windows PowerShell 5.1 lit alors mal les accents).");
                var texteAvant = File.ReadAllText(Path.Combine(DossierScripts, s));
                var texteApres = File.ReadAllText(Path.Combine(copie, "scripts", s));
                if (texteAvant.Contains(".SYNOPSIS", StringComparison.Ordinal)) Assert.Contains(".SYNOPSIS", texteApres, StringComparison.Ordinal);
                if (Regex.IsMatch(texteAvant, @"(?mi)^#Requires\b")) Assert.Matches(@"(?mi)^#Requires\b", texteApres);
                // Hors du bloc d'aide, plus aucune ligne qui ne soit qu'un commentaire
                var horsAide = Regex.Replace(texteApres, @"<#.*?#>", "", RegexOptions.Singleline);
                Assert.DoesNotMatch(@"(?mi)^[ \t]*#(?!Requires\b)", horsAide);
            }
        }
        finally
        {
            try { Directory.Delete(copie, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>Projet et tous les projets qu'il référence, de proche en proche (noms des assemblys publiés avec lui).</summary>
    private static HashSet<string> Fermeture(string projet)
    {
        var vus = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aVoir = new Queue<string>([projet]);
        while (aVoir.Count > 0)
        {
            var p = aVoir.Dequeue();
            if (!vus.Add(p)) continue;
            var csproj = File.ReadAllText(Path.Combine(BaseJetableM4.RacineDepot(), "src", p, p + ".csproj"));
            foreach (Match m in Regex.Matches(csproj, @"<ProjectReference\s+Include=""[^""]*\\(?<nom>[^\\""]+)\.csproj"""))
                aVoir.Enqueue(m.Groups["nom"].Value);
        }
        return vus;
    }

    /// <summary>
    /// Lot P2 : tout assembly maison publié avec l'API ou le poste est dans la liste obfusquée de creer_livraison.ps1 (un projet ajouté
    /// partirait sinon en clair ; le script le refuse aussi à la fabrication), et l'outil est toujours appelé avec -licensed (jamais un
    /// fichier de la version d'essai, qui cesse de fonctionner après 14 jours).
    /// </summary>
    [Fact]
    public void La_livraison_obfusque_tous_nos_assemblys_avec_l_outil_en_version_complete()
    {
        var texte = File.ReadAllText(Path.Combine(DossierScripts, "mep", "obfusquer_livraison.ps1"));
        var liste = Regex.Match(texte, @"\$assemblysObfusques = @\((?<corps>[^)]*)\)");
        Assert.True(liste.Success, "Liste $assemblysObfusques introuvable dans obfusquer_livraison.ps1.");
        // La livraison d'officine et le paquet d'essai passent tous deux par ce script, sauf demande explicite (-SansObfuscation)
        foreach (var fabrication in new[] { Path.Combine("mep", "creer_livraison.ps1"), "creer_paquet_essai.ps1" })
        {
            var f = File.ReadAllText(Path.Combine(DossierScripts, fabrication));
            Assert.Contains("obfusquer_livraison.ps1", f);
            Assert.Contains("-ChercherOutil", f);
            Assert.Contains("$SansObfuscation", f);
        }
        var obfusques = Regex.Matches(liste.Groups["corps"].Value, "\"(?<f>[^\"]+)\"").Select(m => m.Groups["f"].Value).Order(StringComparer.Ordinal).ToList();

        var attendus = Fermeture("Pharma.Api").Select(p => $@"Api\{p}.dll")
            .Concat(Fermeture("Pharma.Desktop").Select(p => $@"Poste\{p}.dll")).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(attendus, obfusques);

        Assert.Matches(@"&\s*\$Obfuscateur\s+-licensed\b", texte);
        Assert.Contains("-obfuscate_public_types 0", texte);   // le public garde son nom : JSON échangé, composants d'écran
        Assert.Contains("-exclude_properties 1", texte);       // aucune propriété renommée : JSON des types non publics
        Assert.Contains("-mapping_file 1", texte);             // correspondances archivées hors livraison, pour décoder une trace
    }

    [Fact]
    public void Le_nettoyage_refuse_les_fichiers_de_l_outil_d_obfuscation_dans_une_livraison()
    {
        var texte = File.ReadAllText(Path.Combine(DossierScripts, "mep", "nettoyer_livraison.ps1"));
        foreach (var extension in new[] { ".nrmap", ".license", ".v3lic" }) Assert.Contains($"\"{extension}\"", texte);
    }

    private static bool Signature(byte[] octets) => octets.Length >= 3 && octets[0] == 0xEF && octets[1] == 0xBB && octets[2] == 0xBF;

    private static (int Code, string Sortie) Lancer(string powershell, string script, string arguments)
    {
        var info = new ProcessStartInfo(powershell, $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" {arguments}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(info)!;
        var erreur = p.StandardError.ReadToEndAsync();
        var sortie = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, sortie + erreur.GetAwaiter().GetResult());
    }
}
