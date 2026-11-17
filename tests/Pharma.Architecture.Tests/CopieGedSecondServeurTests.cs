using System.Text;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Copie de la GED vers le 2e serveur : contenu des scripts scripts/second-serveur/copier-ged.ps1 (tire les fichiers, sans jamais rien
/// supprimer) et partager-ged.ps1 (partage en lecture seule et pare-feu 445 limité à une adresse). Aucune commande n'est exécutée.
/// </summary>
public class CopieGedSecondServeurTests
{
    private static string Copier() => Lire("copier-ged.ps1");
    private static string Partager() => Lire("partager-ged.ps1");

    private static string Lire(string nom) => File.ReadAllText(Path.Combine(RacineDepot(), "scripts", "second-serveur", nom));

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    /// <summary>Texte du script sans les commentaires d'aide (bloc &lt;# #&gt;) ni les lignes de commentaire.</summary>
    private static string Code(string script)
    {
        var sansAide = Regex.Replace(script, @"<#.*?#>", "", RegexOptions.Singleline);
        return string.Join('\n', sansAide.Split('\n').Where(l => !l.TrimStart().StartsWith('#')));
    }

    [Fact]
    public void Copier_ne_supprime_jamais_rien_ni_miroir_ni_purge()
    {
        var code = Code(Copier());
        Assert.DoesNotContain("/MIR", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/PURGE", code, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/MOV", code, StringComparison.OrdinalIgnoreCase);   // /MOV et /MOVE suppriment la source
        Assert.DoesNotContain("Remove-Item -Recurse", code);
        // Seul Remove-Item admis : le fichier de travail temporaire de robocopy
        foreach (Match m in Regex.Matches(code, @"Remove-Item[^\n]*"))
            Assert.Contains("-LiteralPath $tmp", m.Value);
        Assert.DoesNotContain("Move-Item", code);
        Assert.DoesNotContain("Clear-Content", code);
        // Ajout et mise à jour seulement, jamais un fichier plus ancien par-dessus un plus récent
        Assert.Contains("\"/E\", \"/XO\"", code);
        Assert.DoesNotContain("/IS", code);   // « même fichier » : forcerait la recopie
        Assert.DoesNotContain("/IT", code);
    }

    [Fact]
    public void Copier_lit_la_source_sans_jamais_y_ecrire()
    {
        var code = Code(Copier());
        // robocopy : la source est le premier argument, la destination le second
        Assert.Contains("@($p.Source, $p.Destination,", code);
        // Aucune commande d'écriture ou de suppression visant la source
        foreach (var commande in new[] { "Remove-Item", "Set-Content", "Add-Content", "Out-File", "New-Item", "Copy-Item", "icacls", "Set-Acl" })
            foreach (Match m in Regex.Matches(code, commande + @"[^\n]*"))
                Assert.DoesNotMatch(@"\$(p\.)?Source\b(?!Ordonnances)", m.Value);
        // Refus si la source est injoignable, avant toute copie
        Assert.Contains("source injoignable", code);
        Assert.True(code.IndexOf("source injoignable", StringComparison.Ordinal) < code.IndexOf("robocopy.exe @robocopy", StringComparison.Ordinal),
            "La source doit être contrôlée avant la copie.");
    }

    [Fact]
    public void Copier_essai_installer_desinstaller_et_tentatives_limitees()
    {
        var script = Copier();
        Assert.Contains("[switch]$Essai", script);
        Assert.Contains("[switch]$Installer", script);
        Assert.Contains("[switch]$Desinstaller", script);
        Assert.Contains("$robocopy += \"/L\"", script);   // simulation : liste sans copier
        Assert.Contains("\"/R:$Tentatives\"", script);
        Assert.Contains("\"/W:$AttenteSecondes\"", script);
        Assert.Contains("[int]   $Tentatives             = 3", script);
        Assert.DoesNotContain("/R:1000000", script);
        // Journal, code de sortie clair
        Assert.Contains("copie-ged-{0:yyyyMM}.log", script);
        Assert.Contains("exit 1", script);
        Assert.Contains("exit $pire", script);
        // Essai : ni journal, ni tâche, ni compte, ni droit
        Assert.Contains("if ($Essai) { return }", script);
        Assert.Contains("aucune tâche, aucun compte, aucun droit", script);
    }

    [Fact]
    public void Copier_tache_planifiee_sans_doublon_compte_dedie_non_administrateur()
    {
        var code = Code(Copier());
        Assert.Contains("Register-ScheduledTask", code);
        Assert.Contains("-RunLevel Limited", code);
        Assert.Contains("-Force", code);                                  // remplace la tâche du même nom : pas de doublon
        Assert.Contains("-MultipleInstances IgnoreNew", code);            // une seule copie à la fois
        Assert.Contains("Unregister-ScheduledTask -TaskName $NomTache", code);
        Assert.Contains("$ToutesLesMinutes", code);
        // Depuis le 2026-11-17 : la tâche est déclenchée chaque minute, la fréquence est le réglage de l'application
        Assert.Contains("-RepetitionInterval (New-TimeSpan -Minutes 1)", code);
        Assert.DoesNotContain("-Minutes $ToutesLesMinutes", code);
        Assert.Contains("[int]   $ToutesLesMinutes       = 0", Copier());
        Assert.DoesNotContain("RunLevel Highest", code);
        Assert.DoesNotMatch(@"-User\s+""?(NT AUTHORITY\\)?SYSTEM", code);
        Assert.DoesNotContain("-Group", code);
        Assert.DoesNotContain("Add-LocalGroupMember", code);             // le compte n'est dans aucun groupe (surtout pas Administrateurs)
        // Droit de modification limité à la GED locale et au dossier de journaux de la copie (jamais à la source)
        Assert.Contains("icacls.exe $d /grant \"${Compte}:(OI)(CI)M\"", code);
        Assert.Contains("$dossiersDroits = @($paires | ForEach-Object { $_.Destination }) + $DossierJournaux", code);
    }

    [Fact]
    public void Copier_frequence_lue_dans_la_base_locale_en_lecture_seule_defaut_5_ne_bloque_jamais()
    {
        var script = Copier();
        var code = Code(script);
        // Lecture seule : une seule requête SELECT, session en lecture seule, sans invite de mot de passe, délai court
        Assert.Contains("SELECT ged_copie_minutes FROM surveillance_replication WHERE id = 1", code);
        Assert.Contains("default_transaction_read_only=on", code);
        Assert.Contains("statement_timeout=5000", code);
        Assert.Contains("PGCONNECT_TIMEOUT = \"5\"", code);
        Assert.Contains("-U $CompteLecture -d $Base -w -X", code);
        foreach (var interdit in new[] { "INSERT ", "UPDATE ", "DELETE ", "ALTER ", "CREATE ", "DROP ", "TRUNCATE ", "GRANT " })
            Assert.DoesNotContain(interdit, code, StringComparison.Ordinal);
        // Compte dédié sans mot de passe : ni postgres, ni le compte de l'API, ni PGPASSWORD
        Assert.Contains("[string]$CompteLecture          = \"pharma_ged_lecture\"", script);
        Assert.DoesNotMatch("CompteLecture\\s*=\\s*\"(postgres|pharma_app)\"", code);
        Assert.DoesNotContain("PGPASSWORD", code);
        Assert.DoesNotContain("pgpass", code, StringComparison.OrdinalIgnoreCase);
        // Défaut 5 minutes, bornes 1 à 60
        Assert.Contains("$MinutesParDefaut = 5", code);
        Assert.Contains("$valeur -lt 1 -or $valeur -gt 60", code);
        // Échec de lecture : défaut, journal, la copie continue (aucun refus ni exit sur ce chemin)
        var debut = code.IndexOf("$minutes = $lu.Minutes", StringComparison.Ordinal);
        var lecture = code[debut..code.IndexOf("$maintenantUtc = ", debut, StringComparison.Ordinal)];
        Assert.Contains("par défaut", lecture);
        Assert.Contains("Ecrire-Journal", lecture);
        Assert.DoesNotContain("Refuser", lecture);
        Assert.DoesNotContain("exit", lecture);
        // Repère de dernière copie réussie ; priorité expliquée dans l'aide ; la tâche n'embarque ni -Maintenant ni -ToutesLesMinutes
        Assert.Contains("derniere-copie-ged.txt", code);
        Assert.Contains("[switch]$Maintenant", script);
        Assert.Contains("-Maintenant (copie tout de suite, repère ignoré) > -ToutesLesMinutes (impose N, pour les essais) > réglage de", script);
        Assert.Contains("la tâche installée n'en porte aucun", script);
        Assert.DoesNotMatch("\\$arguments[^\\n]*-(Maintenant|ToutesLesMinutes)", code);
        Assert.Contains("Refuser \"-ToutesLesMinutes et -Maintenant ne servent qu'à copier en ligne de commande", code);
    }

    [Fact]
    public void Autoriser_lecture_role_dedie_sans_mot_de_passe_boucle_locale_seulement_une_seule_colonne()
    {
        var script = Lire("autoriser-lecture-copie-ged.ps1");
        var code = Code(script);
        Assert.Contains("[string]$Role      = \"pharma_ged_lecture\"", script);
        Assert.Contains("[switch]$Essai", script);
        // Rôle : connexion seulement, aucun droit, pas de mot de passe
        Assert.Contains("CREATE ROLE %I LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION CONNECTION LIMIT 5", code);
        Assert.DoesNotContain("PASSWORD", code.Replace("MotDePasse", "").Replace("mdp", ""), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SET default_transaction_read_only = on", code);
        // Droit : cette seule colonne, jamais la table
        Assert.Contains("GRANT SELECT (ged_copie_minutes) ON public.surveillance_replication TO %I", code);
        Assert.DoesNotMatch("GRANT\\s+(ALL|INSERT|UPDATE|DELETE)", code);
        // pg_hba.conf : trust pour ce rôle seul, boucle locale seulement, jamais « all », jamais un réseau
        Assert.Contains("host    $Base    $Role    127.0.0.1/32    trust", code);
        Assert.Contains("host    $Base    $Role    ::1/128         trust", code);
        Assert.Equal(2, Regex.Matches(code, "\\btrust\\b").Count - Regex.Matches(code, "# .*trust").Count);
        Assert.DoesNotContain("0.0.0.0", code);
        Assert.DoesNotMatch("host\\s+\\S+\\s+all\\b", code);
        Assert.Contains("Copy-Item -LiteralPath $hba", code);                 // copie gardée avant toute modification
        Assert.Contains("pg_reload_conf()", code);
        Assert.DoesNotContain("Restart-Service", code);
        Assert.DoesNotContain("Remove-Item", code);
        // Superutilisateur : mot de passe saisi, jamais écrit
        Assert.Contains("Read-MotDePasse", code);
        Assert.DoesNotMatch("(Write-Host|Write-Warning|Write-Etape|Out-File|Set-Content|Add-Content)[^\\n]*\\$mdpSuper", code);
        // La migration ne crée aucun rôle de connexion
        var migration = File.ReadAllText(Path.Combine(RacineDepot(), "database", "migration", "143_copie_ged_second_serveur_frequence.sql"));
        Assert.DoesNotMatch("(?im)^\\s*CREATE\\s+(ROLE|USER)", migration);
    }

    // ---- Essai local réel : « copie seulement si N minutes sont passées », entre deux dossiers temporaires ----
    // (aucune base, aucune tâche planifiée, aucun partage, aucune règle de pare-feu : le réglage est illisible (défaut 5) ou simulé par -ToutesLesMinutes)

    private sealed class EssaiLocal : IDisposable
    {
        public string Racine { get; } = Path.Combine(Path.GetTempPath(), "essai-copie-ged-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Racine, "source");
        public string Destination => Path.Combine(Racine, "destination");
        public string Journaux => Path.Combine(Racine, "journaux");
        public string Repere => Path.Combine(Journaux, "derniere-copie-ged.txt");

        public EssaiLocal()
        {
            Directory.CreateDirectory(Path.Combine(Source, "sous-dossier"));
            Directory.CreateDirectory(Destination);
            Directory.CreateDirectory(Journaux);
            Depose("a.txt");
            Depose(Path.Combine("sous-dossier", "b.txt"));
        }

        public void Depose(string nom) => File.WriteAllText(Path.Combine(Source, nom), "contenu " + nom);
        public bool Copie(string nom) => File.Exists(Path.Combine(Destination, nom));

        /// <summary>Repère : dernière copie réussie il y a « minutes » minutes.</summary>
        public void DerniereCopieIlYa(double minutes, string lecture = "ok") =>
            File.WriteAllLines(Repere, [$"lecture={lecture}", "copie=" + DateTime.UtcNow.AddMinutes(-minutes).ToString("o")]);

        /// <summary>Lance copier-ged.ps1 ; sans base : -PgBin vers un dossier vide, donc réglage illisible (défaut 5 minutes).</summary>
        public int Lancer(string? source = null, params string[] options)
        {
            var info = new System.Diagnostics.ProcessStartInfo("powershell.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(RacineDepot(), "scripts", "second-serveur", "copier-ged.ps1"),
                         "-Source", source ?? Source, "-Destination", Destination, "-DossierJournaux", Journaux, "-Tentatives", "0", "-AttenteSecondes", "1",
                         "-PgBin", Path.Combine(Racine, "sans-postgresql") }.Concat(options))
                info.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(info)!;
            var sortie = p.StandardOutput.ReadToEndAsync();
            var erreur = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120_000)) { p.Kill(true); throw new TimeoutException("copier-ged.ps1 n'a pas terminé."); }
            Task.WaitAll(sortie, erreur);
            return p.ExitCode;
        }

        public string Journal => Directory.GetFiles(Journaux, "copie-ged-*.log").Select(File.ReadAllText).FirstOrDefault() ?? "";
        public int Lignes(string motif) => Journal.Split('\n').Count(l => l.Contains(motif));
        public void Dispose() { try { Directory.Delete(Racine, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    private static bool EssaiPossible => OperatingSystem.IsWindows() && File.Exists(Path.Combine(Environment.SystemDirectory, "robocopy.exe"));

    [Fact]
    public void Essai_reel_premiere_copie_puis_rien_avant_N_minutes_puis_copie_quand_N_est_passe()
    {
        if (!EssaiPossible) return;   // Windows et robocopy requis
        using var e = new EssaiLocal();

        // Aucun repère : copie immédiate (réglage illisible : 5 minutes par défaut), repère écrit
        Assert.Equal(0, e.Lancer());
        Assert.True(e.Copie("a.txt")); Assert.True(e.Copie(Path.Combine("sous-dossier", "b.txt")));
        Assert.True(File.Exists(e.Repere));
        Assert.Contains("fréquence : 5 min (défaut : réglage illisible)", e.Journal);

        // Juste après : rien (le nouveau fichier n'est pas copié) ; aucune ligne de copie en plus dans le journal
        e.Depose("c.txt");
        var copies = e.Lignes("| GED |");
        Assert.Equal(0, e.Lancer());
        Assert.False(e.Copie("c.txt"));
        Assert.Equal(copies, e.Lignes("| GED |"));

        // 4 minutes écoulées, fréquence de 5 minutes (défaut) : toujours rien
        e.DerniereCopieIlYa(4, "echec");
        Assert.Equal(0, e.Lancer());
        Assert.False(e.Copie("c.txt"));

        // 6 minutes écoulées : copie
        e.DerniereCopieIlYa(6, "echec");
        Assert.Equal(0, e.Lancer());
        Assert.True(e.Copie("c.txt"));
    }

    [Fact]
    public void Essai_reel_reglage_simule_N_impose_et_copie_immediate()
    {
        if (!EssaiPossible) return;
        using var e = new EssaiLocal();
        Assert.Equal(0, e.Lancer(null, "-Maintenant"));
        Assert.True(e.Copie("a.txt"));
        Assert.Contains("copie immédiate (-Maintenant)", e.Journal);

        // Réglage simulé à 1 minute : 30 secondes écoulées -> rien ; 2 minutes -> copie
        e.Depose("d.txt"); e.DerniereCopieIlYa(0.5);
        Assert.Equal(0, e.Lancer(null, "-ToutesLesMinutes", "1"));
        Assert.False(e.Copie("d.txt"));
        e.DerniereCopieIlYa(2);
        Assert.Equal(0, e.Lancer(null, "-ToutesLesMinutes", "1"));
        Assert.True(e.Copie("d.txt"));

        // Réglage simulé à 60 minutes : 59 minutes écoulées -> rien ; -Maintenant l'emporte et copie quand même
        e.Depose("f.txt"); e.DerniereCopieIlYa(59);
        Assert.Equal(0, e.Lancer(null, "-ToutesLesMinutes", "60"));
        Assert.False(e.Copie("f.txt"));
        Assert.Equal(0, e.Lancer(null, "-ToutesLesMinutes", "60", "-Maintenant"));
        Assert.True(e.Copie("f.txt"));

        // Valeur hors bornes : refus avant toute copie
        Assert.Equal(1, e.Lancer(null, "-ToutesLesMinutes", "61"));
    }

    [Fact]
    public void Essai_reel_jamais_de_suppression_et_source_injoignable_retentee_apres_N_minutes_seulement()
    {
        if (!EssaiPossible) return;
        using var e = new EssaiLocal();
        Assert.Equal(0, e.Lancer(null, "-Maintenant"));
        File.Delete(Path.Combine(e.Source, "a.txt"));           // supprimé à la source : reste à la destination
        e.Depose("g.txt");
        Assert.Equal(0, e.Lancer(null, "-Maintenant"));
        Assert.True(e.Copie("a.txt")); Assert.True(e.Copie("g.txt"));

        // Source injoignable : refus (code 1), repère de copie conservé, nouvelle tentative seulement après N minutes
        var absente = e.Source + "-absente";
        e.DerniereCopieIlYa(10);
        Assert.Equal(1, e.Lancer(absente, "-ToutesLesMinutes", "5"));
        Assert.Contains("source injoignable", e.Journal);
        var refus = e.Lignes("REFUS");
        Assert.Equal(0, e.Lancer(absente, "-ToutesLesMinutes", "5"));   // une minute après : pas de nouveau refus
        Assert.Equal(refus, e.Lignes("REFUS"));
        var repere = File.ReadAllText(e.Repere);
        Assert.Contains("copie=", repere);
        Assert.Contains("echec=", repere);
    }

    [Fact]
    public void Aucun_mot_de_passe_en_clair_dans_les_deux_scripts()
    {
        foreach (var script in new[] { Copier(), Partager() })
        {
            var code = Code(script);
            Assert.DoesNotMatch(@"-Password\s+[""']", code);                            // pas de mot de passe littéral
            Assert.DoesNotMatch(@"\$(mdp|motDePasse|password)\w*\s*=\s*[""'][^""']+[""']", code);
            Assert.DoesNotMatch(@"ConvertTo-SecureString\s+[""']", code);
            Assert.DoesNotMatch(@"(Write-Host|Write-Warning|Write-Etape|Ecrire-Journal|Out-File|Set-Content|Add-Content)[^\n]*\$mdp", code);
            Assert.DoesNotContain("cmdkey", code);
            Assert.DoesNotContain("net use", code);                                     // le mot de passe passerait en ligne de commande
            Assert.DoesNotMatch(@"\[string\]\$(Mdp|MotDePasse|Password)", code);       // pas de paramètre de mot de passe
        }
        // Le mot de passe est saisi à l'écran (jamais dans les paramètres de la tâche)
        Assert.Contains("Read-MotDePasse", Copier());
        Assert.Contains("Read-MotDePasse", Partager());
        Assert.DoesNotMatch(@"\$arguments\s*[+]?=[^\n]*\$mdp", Copier());
    }

    [Fact]
    public void Partager_lecture_seule_pour_le_seul_compte_de_copie_jamais_tout_le_monde()
    {
        var code = Code(Partager());
        Assert.Contains("New-SmbShare", code);
        Assert.Contains("-ReadAccess $Compte", code);
        Assert.DoesNotMatch(@"-(Full|Change)Access", code);
        Assert.DoesNotMatch(@"-ReadAccess\s+[""']?(Everyone|Tout le monde|Users|Utilisateurs|Authenticated|\*)", code);
        Assert.DoesNotMatch(@"Grant-SmbShareAccess", code);
        Assert.DoesNotMatch(@"(?i)Everyone|Tout le monde|S-1-1-0", code);
        Assert.Contains("-EncryptData $true", code);
        Assert.Contains("-FolderEnumerationMode AccessBased", code);
        // NTFS : lecture et exécution seulement, en ajout
        Assert.Contains("icacls.exe $p.Dossier /grant \"${Compte}:(OI)(CI)RX\"", code);
        Assert.DoesNotMatch(@"icacls[^\n]*/(inheritance|remove|deny|reset|setowner)", code);
        // Contrôle après création : tout autre compte est signalé
        Assert.Contains("Get-SmbShareAccess", code);
        Assert.Contains("Write-Warning", code);
        // Compte sans groupe
        Assert.DoesNotContain("Add-LocalGroupMember", code);
    }

    [Fact]
    public void Partager_pare_feu_445_adresse_unique_profil_prive_sans_doublon()
    {
        var script = Partager();
        var code = Code(script);
        Assert.Matches(@"\[Parameter\(Mandatory\)\]\s*\[string\]\$AdresseSecond\s*,", script);
        Assert.Contains("TryParse($AdresseSecond", script);
        Assert.Contains("ni plage, ni masque, ni « Any »", script);
        Assert.Contains("[int]   $Port = 445", script);
        Assert.Matches(@"-Direction Inbound\s+-Protocol TCP\s+-LocalPort \$Port", code);
        Assert.Contains("-RemoteAddress $AdresseSecond", code);
        Assert.Contains("-Action Allow", code);
        Assert.Contains("-Profile Private", code);
        Assert.DoesNotMatch(@"-RemoteAddress\s+(Any|\*|""?LocalSubnet)", code);
        Assert.DoesNotContain("-Profile Any", code);
        Assert.DoesNotContain("-Profile Public", code);
        // Nom fixe, ancienne règle retirée avant la création
        Assert.Contains("$NomRegle = \"PharmaOfficine SMB 445 GED (2e serveur)\"", code);
        Assert.True(code.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal) < code.IndexOf("New-NetFirewallRule", StringComparison.Ordinal),
            "L'ancienne règle doit être retirée avant la création de la nouvelle.");
        // Avertissement si une autre règle ouvre 445
        Assert.Contains("Get-NetFirewallPortFilter", code);
        Assert.Contains("OUVERTE À TOUT LE RÉSEAU", script);
        Assert.Contains("$_.DisplayName -ne $NomRegle", code);
    }

    [Fact]
    public void Partager_essai_et_whatif_et_chaque_action_protegee()
    {
        var script = Partager();
        Assert.Contains("[CmdletBinding(SupportsShouldProcess", script);
        Assert.Contains("[switch]$Essai", script);
        Assert.Contains("if ($Essai) { $WhatIfPreference = $true }", script);
        Assert.Contains("[SIMULATION]", script);
        Assert.Contains("[switch]$Retirer", script);
        var code = Code(script);
        foreach (var commande in new[] { "New-SmbShare", "Remove-SmbShare -Name $p.Nom", "New-NetFirewallRule", "New-LocalUser", "icacls.exe $p.Dossier /grant" })
            foreach (var index in Occurrences(code, commande))
                Assert.Contains("$PSCmdlet.ShouldProcess(", code[Math.Max(0, index - 900)..index]);
        Assert.Contains("Remove-NetFirewallRule", code);
        // La suppression du partage ne touche jamais aux documents
        Assert.DoesNotContain("Remove-Item", code);
    }

    [Fact]
    public void Scripts_en_utf8_avec_signature_pour_windows_powershell_5_et_style_commun()
    {
        foreach (var nom in new[] { "copier-ged.ps1", "partager-ged.ps1", "autoriser-lecture-copie-ged.ps1" })
        {
            var octets = File.ReadAllBytes(Path.Combine(RacineDepot(), "scripts", "second-serveur", nom));
            Assert.True(octets.Length > 3 && octets[0] == 0xEF && octets[1] == 0xBB && octets[2] == 0xBF, $"{nom} : UTF-8 avec signature (BOM) requis.");
            Assert.Contains(". (Join-Path $PSScriptRoot \"commun.ps1\")", Encoding.UTF8.GetString(octets));
        }
    }

    private static IEnumerable<int> Occurrences(string texte, string motif)
    {
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + 1, StringComparison.Ordinal))
            yield return i;
    }
}
