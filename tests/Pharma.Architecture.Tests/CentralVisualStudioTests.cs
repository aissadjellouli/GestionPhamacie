using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Lancement de l'API, du poste et du serveur central depuis Visual Studio (profil « API + Poste + Central ») et outil de préparation
/// scripts/dev/preparer_central_vs.ps1 : garde-fous vérifiés sur le contenu des fichiers, sans rien exécuter
/// (docs/poste-de-developpement.md, « Lancer API + Poste + Central dans Visual Studio »).
/// </summary>
public partial class CentralVisualStudioTests
{
    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string Chemin => Path.Combine(RacineDepot(), "scripts", "dev", "preparer_central_vs.ps1");

    private static string Script => File.ReadAllText(Chemin);

    /// <summary>Code sans l'aide en bloc ni les lignes de commentaire.</summary>
    private static string Code() =>
        string.Join('\n', BlocAide().Replace(Script, "").Split('\n').Where(l => !l.TrimStart().StartsWith('#')));

    [GeneratedRegex(@"<#.*?#>", RegexOptions.Singleline)]
    private static partial Regex BlocAide();

    private static string Corps(string code, string fonction)
    {
        var d = code.IndexOf("function " + fonction, StringComparison.Ordinal);
        Assert.True(d >= 0, fonction);
        return code[d..code.IndexOf("\n}", d, StringComparison.Ordinal)];
    }

    [Fact]
    public void Le_script_existe_dit_qu_il_n_est_jamais_pour_la_production_et_propose_WhatIf()
    {
        Assert.True(File.Exists(Chemin));
        var debut = Script.TrimStart('﻿');
        Assert.StartsWith("<#", debut);
        Assert.Contains("JAMAIS DE PRODUCTION", debut[..600]);
        foreach (var rubrique in new[] { ".SYNOPSIS", ".DESCRIPTION", ".PARAMETER Preparer", ".PARAMETER Activer", ".PARAMETER Etat", ".PARAMETER Retirer", ".EXAMPLE" })
            Assert.Contains(rubrique, debut);
        Assert.Matches(@"\[CmdletBinding\([^)]*SupportsShouldProcess\s*=\s*\$true", Script);
        // En simulation, -Preparer rend la main avant toute création (dossier, variable, rôle, compilation)
        var code = Code();
        var p = Corps(code, "Invoke-Preparer");
        var retour = p.IndexOf("return", p.IndexOf("Plan (simulation", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(retour > 0);
        foreach (var creation in new[] { "New-Item", "Set-DossierRestreint", "Set-VariableUtilisateur", "CREATE ROLE", "dotnet build", "Invoke-CodeActivation" })
            Assert.True(retour < p.IndexOf(creation, StringComparison.Ordinal), $"{creation} avant la fin de la simulation");
        var r = Corps(code, "Invoke-Retirer");
        var retourR = r.IndexOf("return", r.IndexOf("Plan (simulation", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(retourR > 0 && retourR < r.IndexOf("SetEnvironmentVariable", StringComparison.Ordinal) && retourR < r.IndexOf("Remove-Item", StringComparison.Ordinal));
    }

    [Fact]
    public void Seules_pharmacie_cloud_dev_et_pharmacie_dev_sont_permises()
    {
        var code = Code();
        Assert.Contains("$BaseCentrale = \"pharmacie_cloud_dev\"", code);
        Assert.Contains("$BaseOfficine = \"pharmacie_dev\"", code);
        var garde = Corps(code, "Assert-BaseAutorisee");
        Assert.Contains("$Base -cne \"pharmacie_cloud_dev\"", garde);
        Assert.Contains("$Base -cne \"pharmacie_dev\"", garde);
        // Aucune base en paramètre ; aucune autre base de démonstration nommée dans le code
        Assert.DoesNotMatch(@"(?im)^\s*\[(string)\]\s*\$Base\w*\s*[,=)]", code.Split("$ErrorActionPreference")[0]);
        Assert.DoesNotContain("pharmacie_test", code);
        // Toute requête vise la base de maintenance (rôles), la base centrale ou celle de l'officine
        foreach (Match m in Regex.Matches(code, @"Invoke-Psql[^\n]*-Base\s+(\S+)"))
            Assert.Contains(m.Groups[1].Value, new[] { "$BaseMaintenance", "$BaseCentrale", "$Base" });
        foreach (Match m in Regex.Matches(code, @"Invoke-Lecture\s+(\$\w+)"))
            Assert.Contains(m.Groups[1].Value, new[] { "$BaseCentrale", "$BaseOfficine" });
        Assert.Contains("Assert-BaseAutorisee $BaseCentrale \"central\"", Corps(code, "Get-VariablesOutil"));
        Assert.Contains("(Get-BaseDeChaine $cxOfficine) -cne \"pharmacie_dev\"", code);
    }

    [Fact]
    public void Jamais_de_DROP_DATABASE_ni_de_restauration_ni_de_compte_applicatif()
    {
        var texte = Script;
        Assert.DoesNotMatch(@"(?i)DROP\s+DATABASE|CREATE\s+DATABASE|dropdb|createdb|pg_restore|PgRestore|pg_dump|PgDump", texte);
        Assert.DoesNotMatch(@"(?i)\b(INSERT|UPDATE|DELETE|TRUNCATE)\s+(INTO\s+|FROM\s+)?\w+", Code().Replace("SELECT", "", StringComparison.Ordinal)
            .Replace("default_transaction_read_only", "", StringComparison.Ordinal));
        Assert.DoesNotMatch(@"(?i)(UPDATE|INSERT\s+INTO|DELETE\s+FROM)\s+utilisateur\b", texte);
        Assert.DoesNotContain("essai_activation", texte);
        Assert.DoesNotContain("/api/auth/connexion", texte);
        // Un seul DROP ROLE, sur le rôle dédié, dans -Retirer
        Assert.Single(Regex.Matches(texte, @"(?i)DROP\s+ROLE"));
        Assert.Contains("DROP ROLE IF EXISTS $RoleCentral", Corps(Code(), "Invoke-Retirer"));
        Assert.Contains("$RoleCentral = \"pharma_cloud_dev_vs\"", Code());
        Assert.DoesNotMatch(@"(?i)(ALTER|DROP)\s+ROLE\s+(IF\s+EXISTS\s+)?pharma_cloud_api\b", texte);
        // Un seul dossier supprimé, le dossier exact hors dépôt, après contrôle
        foreach (Match m in Regex.Matches(Code(), @"Remove-Item[^\n]*-Recurse[^\n]*"))
            Assert.Contains("-LiteralPath $Racine", m.Value);
        Assert.Contains("$Racine = Join-Path $env:LOCALAPPDATA \"PharmaOfficine\\central-dev\"", Code());
        // Aucun processus arrêté (Visual Studio et ce qu'il lance ne sont jamais touchés)
        Assert.DoesNotMatch(@"(?i)Stop-Process|taskkill|\.Kill\(", texte);
    }

    [Fact]
    public void Aucun_secret_dans_le_depot_et_jamais_la_connexion_du_proprietaire_au_niveau_utilisateur()
    {
        var texte = Script;
        var code = Code();
        // Aucun mot de passe en clair, en paramètre, ni écrit dans un fichier ; pas le mot de passe de développement documenté
        Assert.DoesNotMatch(@"(?i)Password=[A-Za-z0-9]", texte);
        Assert.DoesNotMatch(@"PGPASSWORD\s*=\s*[""'][^""'$]", texte);
        Assert.DoesNotContain("pharma_dev", texte);
        Assert.DoesNotMatch(@"(?im)^\s*(\[Parameter[^\]]*\]\s*)?\[(string|securestring)\]\s*\$\w*(MotDePasse|Password|Mdp|Secret)\w*", code.Split("$ErrorActionPreference")[0]);
        Assert.DoesNotMatch(@"(?i)(Set-Content|Out-File|Add-Content|Write-Host|Write-Ok|Write-Info|Write-Simul)[^\n]*\$(mdpAutorite|mdpRole|MotDePasseAutorite|cxOfficine)\b", code);
        Assert.Contains("RNGCryptoServiceProvider", code);
        // etat.json : seulement le code provisoire, le libellé et la base
        Assert.Contains("[pscustomobject]@{ OfficineProvisoire = $guid; Libelle = $LibelleOfficine; Base = $BaseOfficine;", code);
        Assert.DoesNotMatch(@"(?i)Set-Content[^\n]*\$Depot|Out-File[^\n]*\$Depot", code);
        // Variables d'utilisateur : liste fermée, sans la connexion du propriétaire
        Assert.Contains("$VariablesUtilisateur = @(\"PHARMA_CLOUD_DOSSIER\", \"PHARMA_CLOUD_MOT_DE_PASSE\", \"PHARMA_CLOUD_CONNEXION\", \"ConnectionStrings__Pharmacie\", \"Synchronisation__Dossier\")", code);
        foreach (Match m in Regex.Matches(code, @"SetEnvironmentVariable\(([^,]+),[^,]+,\s*""User""\)"))
            Assert.Contains(m.Groups[1].Value.Trim(), new[] { "$Nom", "$n" });
        Assert.Contains("$VariablesUtilisateur -notcontains $Nom", Corps(code, "Set-VariableUtilisateur"));
        Assert.DoesNotMatch(@"PHARMA_CLOUD_ADMIN_CONNEXION[^\n]*""User""", code);
        Assert.Contains("PHARMA_CLOUD_ADMIN_CONNEXION = (Get-ChaineConnexion $BaseCentrale $CompteBase", Corps(code, "Get-VariablesOutil"));
        // Compte postgres jamais utilisé, superutilisateur refusé
        Assert.Contains("$CompteBase = \"pharma_app\"", code);
        Assert.DoesNotMatch(@"(?i)-Compte\s+[""']?postgres|-U\s+[""']?postgres|Username=postgres", code);
        Assert.Contains("ce script refuse de s'en servir", code);
        // Outil compilé hors du dépôt (Visual Studio verrouille bin)
        Assert.Contains("--artifacts-path $Outil", code);
        Assert.Contains("Assert-DossierRacine", Corps(code, "Invoke-Preparer"));
    }

    [Fact]
    public void Le_profil_du_central_n_a_aucun_secret_et_la_solution_a_les_deux_profils()
    {
        var racine = RacineDepot();
        var launch = File.ReadAllText(Path.Combine(racine, "src", "Pharma.Cloud.Api", "Properties", "launchSettings.json"));
        using (var doc = JsonDocument.Parse(launch))
        {
            var profil = doc.RootElement.GetProperty("profiles").GetProperty("Pharma.Cloud.Api");
            Assert.Equal("Project", profil.GetProperty("commandName").GetString());
            Assert.False(profil.GetProperty("launchBrowser").GetBoolean());
            Assert.False(profil.TryGetProperty("applicationUrl", out _));      // ignorée : le central écoute sur Cloud:Adresse / Cloud:Port
            var variables = profil.GetProperty("environmentVariables").EnumerateObject().ToList();
            Assert.Single(variables);
            Assert.Equal("ASPNETCORE_ENVIRONMENT", variables[0].Name);
            Assert.Equal("Development", variables[0].Value.GetString());
        }
        Assert.DoesNotMatch(@"(?i)password|mot_de_passe|PHARMA_CLOUD|ConnectionStrings", launch);

        using var sln = JsonDocument.Parse(File.ReadAllText(Path.Combine(racine, "PharmaOfficine.slnLaunch")));
        var profils = sln.RootElement.EnumerateArray().ToDictionary(p => p.GetProperty("Name").GetString()!, p => p.GetProperty("Projects"));
        Assert.True(profils.ContainsKey("API + Poste client"));
        Assert.True(profils.ContainsKey("API + Poste + Central"));
        var projets = profils["API + Poste + Central"].EnumerateArray().Select(p => p.GetProperty("Path").GetString()).ToList();
        Assert.Equal(new[] { @"src\Pharma.Cloud.Api\Pharma.Cloud.Api.csproj", @"src\Pharma.Api\Pharma.Api.csproj", @"src\Pharma.Desktop\Pharma.Desktop.csproj" }, projets);
        Assert.Equal(new[] { @"src\Pharma.Api\Pharma.Api.csproj", @"src\Pharma.Desktop\Pharma.Desktop.csproj" },
            profils["API + Poste client"].EnumerateArray().Select(p => p.GetProperty("Path").GetString()).ToList());
    }

    [Fact]
    public void Les_documents_sont_relies()
    {
        var guide = File.ReadAllText(Path.Combine(RacineDepot(), "docs", "poste-de-developpement.md"));
        Assert.Contains("Lancer API + Poste + Central dans Visual Studio", guide);
        foreach (var mot in new[] { @"scripts\dev\preparer_central_vs.ps1", "-Preparer", "-Activer", "-Etat", "-Retirer", "API + Poste + Central", "pharma_cloud_dev_vs", "https://127.0.0.1:5090/" })
            Assert.Contains(mot, guide);
        Assert.Contains("preparer_central_vs.ps1", File.ReadAllText(Path.Combine(RacineDepot(), "docs", "essai-cloud-officine.md")));
    }
}
