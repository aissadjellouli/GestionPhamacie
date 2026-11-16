using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Outil d'essai local scripts/dev/essai_cloud_officine.ps1 (officine d'essai et serveur central d'essai sur ce PC) : garde-fous
/// vérifiés sur le contenu du script. Seuls -WhatIf et un refus de port sont exécutés, sans effet (docs/essai-cloud-officine.md).
/// </summary>
public partial class EssaiCloudOfficineTests
{
    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Chemin => Path.Combine(RacineDepot(), "scripts", "dev", "essai_cloud_officine.ps1");

    private static string Script => File.ReadAllText(Chemin);

    /// <summary>Code sans l'aide en bloc ni les lignes de commentaire.</summary>
    private static string Code() =>
        string.Join('\n', BlocAide().Replace(Script, "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [GeneratedRegex(@"<#.*?#>", RegexOptions.Singleline)]
    private static partial Regex BlocAide();

    [Fact]
    public void Le_script_existe_et_dit_en_tete_qu_il_n_est_jamais_pour_la_production()
    {
        Assert.True(File.Exists(Chemin));
        var debut = Script.TrimStart('﻿');
        Assert.StartsWith("<#", debut);
        Assert.Contains("JAMAIS DE PRODUCTION", debut[..600]);
        foreach (var rubrique in new[] { ".SYNOPSIS", ".DESCRIPTION", ".PARAMETER Demarrer", ".EXAMPLE" })
            Assert.Contains(rubrique, debut);
        Assert.True(File.Exists(Path.Combine(RacineDepot(), "docs", "essai-cloud-officine.md")));
    }

    [Fact]
    public void Les_ports_interdits_sont_refuses_et_les_ports_par_defaut_sont_ceux_demandes()
    {
        var code = Code();
        Assert.Contains("$PortsInterdits = @(5080, 5180, 9333, 5186, 9446, 5432)", code);
        Assert.Contains("[int]$PortCentral = 5190", code);
        Assert.Contains("[int]$PortApi = 5191", code);
        Assert.Contains("function Test-PortInterdit", code);
        Assert.Contains("function Test-PortLibre", code);
        // Les deux ports sont contrôlés avant toute création
        var contrôle = code.IndexOf("Test-PortInterdit $p", StringComparison.Ordinal);
        Assert.True(contrôle > 0);
        Assert.True(contrôle < code.IndexOf("New-Item", StringComparison.Ordinal), "Les ports sont contrôlés avant de créer quoi que ce soit.");
        Assert.Contains("déjà occupé", code);
    }

    [Fact]
    public void Un_port_interdit_est_refuse_sans_rien_creer()
    {
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(powershell)) return;
        var dossierEssai = Path.Combine(Path.GetTempPath(), "pharma-essai-cloud");
        var existaitAvant = Directory.Exists(dossierEssai);
        foreach (var port in new[] { 5080, 5180, 9333, 5186, 9446 })
        {
            var (code, sortie) = Lancer(powershell, $"-Demarrer -PortCentral {port} -WhatIf");
            Assert.Equal(1, code);
            Assert.Contains($"port {port} interdit", sortie);
            var (code2, sortie2) = Lancer(powershell, $"-Demarrer -PortApi {port} -WhatIf");
            Assert.Equal(1, code2);
            Assert.Contains($"port {port} interdit", sortie2);
        }
        Assert.Equal(existaitAvant, Directory.Exists(dossierEssai));
    }

    [Fact]
    public void WhatIf_ne_cree_rien_et_annonce_le_plan()
    {
        var code = Code();
        Assert.Matches(@"\[CmdletBinding\([^)]*SupportsShouldProcess\s*=\s*\$true", Script);
        Assert.Contains("$WhatIfPreference", code);
        Assert.Contains("Plan (simulation", code);
        // En simulation, la fonction rend la main AVANT le premier accès à PostgreSQL et la première création
        var d = code.IndexOf("function Invoke-Demarrer", StringComparison.Ordinal);
        var retour = code.IndexOf("return", code.IndexOf("Plan (simulation", d, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(retour > 0 && retour < code.IndexOf("Initialize-Pg", d, StringComparison.Ordinal));
        var a = code.IndexOf("function Invoke-Arreter", StringComparison.Ordinal);
        var retourArret = code.IndexOf("return", code.IndexOf("Plan (simulation", a, StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(retourArret > 0 && retourArret < code.IndexOf("Stop-Process", a, StringComparison.Ordinal));

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!OperatingSystem.IsWindows() || !File.Exists(powershell)) return;
        var dossierEssai = Path.Combine(Path.GetTempPath(), "pharma-essai-cloud");
        if (Directory.Exists(dossierEssai)) return;   // un essai réel est en cours : ne rien lancer
        var (c1, s1) = Lancer(powershell, "-Demarrer -WhatIf");
        Assert.Equal(0, c1);
        Assert.Contains("[simulation]", s1);
        Assert.False(Directory.Exists(dossierEssai));
        var (c2, s2) = Lancer(powershell, "-Arreter -WhatIf");
        Assert.Equal(0, c2);
        Assert.False(Directory.Exists(dossierEssai));
    }

    [Fact]
    public void Idempotence_arreter_sans_essai_ne_fait_rien_et_demarrer_reprend_ce_qui_manque()
    {
        var code = Code();
        Assert.Contains("Aucun essai en cours : rien à faire.", code);
        Assert.Contains("reprise de ce qui manque", code);
        Assert.Contains("déjà en marche", code.Replace("Déjà en marche", "déjà en marche"));
        Assert.Contains("Test-BaseExiste $BaseOfficine", code);       // la copie n'est pas refaite
        Assert.Contains("-GarderBases", Script);
    }

    [Fact]
    public void Jamais_de_pharmacie_dev_ni_pharmacie_test_en_ecriture()
    {
        var code = Code();
        // pharmacie_dev : une seule constante, lue par pg_dump
        Assert.Equal(1, Regex.Matches(code, @"pharmacie_dev").Count);
        Assert.Contains("$BaseSource = \"pharmacie_dev\"", code);
        Assert.Matches(@"PgDump[^\n]*\$BaseSource\)", code);
        // Toute commande qui écrit vise une base d'essai ou la base de maintenance, jamais $BaseSource ni pharmacie_test
        foreach (Match m in Regex.Matches(code, @"Invoke-Psql[^\n]*-Base\s+(\S+)"))
            Assert.Contains(m.Groups[1].Value, new[] { "$BaseCentrale", "$BaseOfficine", "$BaseMaintenance", "$Base" });
        Assert.DoesNotContain("pharmacie_test", code);
        Assert.DoesNotContain("pharmacie_cloud_dev", code);
        Assert.DoesNotMatch(@"Invoke-Psql[^\n]*\$BaseSource", code);
        // pg_restore et DROP/CREATE : jamais vers la source
        Assert.Matches(@"PgRestore[^\n]*""-d"", \$BaseOfficine", code);
        Assert.DoesNotMatch(@"(?i)(UPDATE|DELETE|INSERT|ALTER|TRUNCATE)[^\n]*\$BaseSource", code);
    }

    [Fact]
    public void Jamais_de_DROP_sur_une_autre_base_que_les_deux_bases_d_essai()
    {
        var code = Code();
        Assert.Contains("$BaseCentrale = \"pharmacie_cloud_essai\"", code);
        Assert.Contains("$BaseOfficine = \"pharmacie_essai_officine\"", code);
        Assert.Contains("$BasesJetables = @($BaseCentrale, $BaseOfficine)", code);
        // Un seul DROP DATABASE, dans la fonction gardée, qui refuse tout nom hors liste
        var drops = Regex.Matches(code, @"(?i)DROP\s+DATABASE");
        Assert.Equal(1, drops.Count);
        var debut = code.IndexOf("function Remove-BaseJetable", StringComparison.Ordinal);
        Assert.True(debut > 0);
        var fin = code.IndexOf("\n}", debut, StringComparison.Ordinal);
        Assert.InRange(drops[0].Index, debut, fin);
        Assert.Contains("$BasesJetables -notcontains $Nom", code[debut..fin]);
        Assert.DoesNotMatch(@"(?i)dropdb", code);
        // Chaque appel de la fonction passe une base d'essai
        foreach (Match m in Regex.Matches(code, @"(?<!function )Remove-BaseJetable\s+(\S+)"))
            Assert.Contains(m.Groups[1].Value.TrimEnd(';', '}'), new[] { "$b" });
        Assert.Contains("foreach ($b in $restes) { Remove-BaseJetable $b", code);
        Assert.Contains("$restes = @($BasesJetables | Where-Object", code);
        // Un seul rôle supprimé, nommé en dur ; un seul dossier supprimé, le dossier d'essai exact
        Assert.Equal(1, Regex.Matches(code, @"(?i)DROP\s+ROLE").Count);
        Assert.Contains("DROP ROLE IF EXISTS $RoleCentral", code);
        Assert.Contains("$RoleCentral = \"pharma_cloud_essai\"", code);
        Assert.Contains("Remove-Item -LiteralPath $DossierEssai -Recurse -Force", code);
        Assert.Contains("(Split-Path $DossierEssai -Leaf) -ne \"pharma-essai-cloud\"", code);
        foreach (Match m in Regex.Matches(code, @"Remove-Item[^\n]*-Recurse[^\n]*"))
            Assert.Contains("$DossierEssai", m.Value);
        // Les PID : arrêt seulement d'un processus de nom attendu, retrouvé par son PID enregistré
        Assert.Matches(@"function Get-ProcessusEssai[\s\S]*?ProcessName -eq \$NomAttendu", code);
        Assert.DoesNotMatch(@"Stop-Process\s+-Name|Get-Process\s+-Name|taskkill|Kill\(\)", code);
    }

    [Fact]
    public void Aucune_adresse_hors_locale_et_aucun_envoi_sur_Internet()
    {
        var code = Code();
        foreach (Match m in Regex.Matches(code, @"https?://([^/:""\s$`]*)"))
            Assert.True(m.Groups[1].Value is "" or "127.0.0.1" or "localhost", $"Adresse non locale : {m.Value}");
        Assert.Contains("$AdresseLocale = \"127.0.0.1\"", code);
        Assert.DoesNotContain("0.0.0.0", code);
        Assert.DoesNotMatch(@"(?i)Host=(?!localhost)", code);
        Assert.DoesNotMatch(@"(?i)Invoke-WebRequest|\bcurl\b|\bwget\b|DownloadString|DownloadFile|Send-MailMessage|New-NetFirewallRule|netsh", code);
        // Les serveurs écoutent sur la boucle locale seulement
        Assert.Contains("$v[\"Cloud__Adresse\"] = $AdresseLocale", code);
        Assert.Contains("Urls = \"http://${AdresseLocale}:$PortApiUtilise\"", code);
        Assert.Contains("\"-h\", \"localhost\"", code);
    }

    [Fact]
    public void Aucun_magasin_de_certificats_aucun_secret_aucun_compte_postgres()
    {
        var texte = Script;
        var code = Code();
        Assert.DoesNotMatch(@"(?i)Cert:\\|X509Store|Import-Certificate|Import-PfxCertificate|certutil|New-SelfSignedCertificate|StoreLocation", code);
        // Aucun mot de passe en paramètre ni écrit en clair
        Assert.DoesNotMatch(@"(?im)^\s*(\[Parameter[^\]]*\]\s*)?\[(string|securestring)\]\s*\$\w*(MotDePasse|Password|Passwd|Mdp|Pwd|Secret)\w*\s*[,=)]?", code.Split("function Write-Etape2")[0]);
        Assert.DoesNotContain("pharma_dev", texte);
        Assert.DoesNotMatch(@"PGPASSWORD\s*=\s*[""'][^""'$]", texte);
        Assert.Contains("Read-Host", texte);
        Assert.Contains("New-MotDePasseEssai", texte);
        Assert.Contains("RNGCryptoServiceProvider", texte);
        Assert.DoesNotMatch(@"(?i)Password=[^$;""\s]", code);
        // Les secrets restent dans le dossier d'essai, hors dépôt
        Assert.Contains("$DossierEssai = Join-Path $env:TEMP \"pharma-essai-cloud\"", code);
        Assert.Contains("$FichierSecrets = Join-Path $DossierEssai \"secrets.json\"", code);
        Assert.DoesNotMatch(@"Set-Content[^\n]*\$Depot|Out-File[^\n]*\$Depot", code);
        // Jamais le compte postgres : compte fixe pharma_app, superutilisateur refusé
        Assert.Contains("$CompteBase = \"pharma_app\"", code);
        Assert.DoesNotMatch(@"(?i)-Compte\s+[""']?postgres|-U\s+[""']?postgres|Username=postgres", code);
        Assert.DoesNotMatch(@"(?i)-CompteAdmin\s+[""']?postgres", code);
        Assert.Contains("ce script refuse de s'en servir", code);
        Assert.Contains("Le script ne s'élève pas lui-même", code);
        // L'administration du central n'est jamais donnée au service
        Assert.Contains("[void]$v.Remove(\"PHARMA_CLOUD_ADMIN_CONNEXION\")", code);
        // Aucun fichier du poste réel : les dossiers de l'API d'essai sont redirigés vers le dossier d'essai
        foreach (var cle in new[] { "Synchronisation__Dossier", "Ged__Dossier", "SuperAdmin__Fichier", "Integrite__FichierCle", "Journaux__Dossier" })
            Assert.Contains(cle, code);
        Assert.DoesNotContain("ProgramData", code);
        Assert.DoesNotContain("smptp", texte);
    }

    [Fact]
    public void Les_documents_sont_relies_entre_eux()
    {
        var guide = File.ReadAllText(Path.Combine(RacineDepot(), "docs", "essai-cloud-officine.md"));
        Assert.Contains("scripts\\dev\\essai_cloud_officine.ps1", guide);
        foreach (var mot in new[] { "-Demarrer", "-Etat", "-Arreter", "-GarderBases", "5190", "5191", "pharmacie_cloud_essai", "pharmacie_essai_officine" })
            Assert.Contains(mot, guide);
        Assert.Contains("essai-cloud-officine.md", File.ReadAllText(Path.Combine(RacineDepot(), "docs", "scripts-base-de-donnees.md")));
        Assert.Contains("essai-cloud-officine.md", File.ReadAllText(Path.Combine(RacineDepot(), "docs", "poste-de-developpement.md")));
    }

    private static (int Code, string Sortie) Lancer(string powershell, string arguments)
    {
        var info = new ProcessStartInfo(powershell, $"-NoProfile -ExecutionPolicy Bypass -File \"{Chemin}\" {arguments}")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        info.Environment.Remove("PGPASSWORD");
        using var p = Process.Start(info)!;
        var sortie = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return (p.ExitCode, sortie);
    }
}
