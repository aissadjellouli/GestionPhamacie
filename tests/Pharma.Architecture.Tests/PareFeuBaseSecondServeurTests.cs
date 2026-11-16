namespace Pharma.Architecture.Tests;

/// <summary>
/// Règle de pare-feu du port PostgreSQL pour le 2e serveur : contenu du script scripts/second-serveur/ouvrir-port-base.ps1
/// (le port 5432 n'est jamais ouvert au reste du réseau). Aucune commande de pare-feu n'est exécutée.
/// </summary>
public class PareFeuBaseSecondServeurTests
{
    private static string Script() => File.ReadAllText(Path.Combine(RacineDepot(), "scripts", "second-serveur", "ouvrir-port-base.ps1"));

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    [Fact]
    public void Adresse_du_second_serveur_obligatoire_et_sans_valeur_par_defaut()
    {
        var script = Script();
        Assert.Matches(@"\[Parameter\(Mandatory\)\]\s*\[string\]\$AdresseSecond\s*,", script);
        Assert.DoesNotMatch(@"\$AdresseSecond\s*=", script.Replace("$AdresseSecond =", "").Split("param(")[1].Split("\n)\n")[0]);
        // Une seule IPv4 unicast : ni plage, ni Any
        Assert.Contains("TryParse($AdresseSecond", script);
        Assert.Contains("ni plage, ni masque, ni « Any »", script);
    }

    [Fact]
    public void Regle_entrante_tcp_profil_prive_limitee_a_l_adresse_distante()
    {
        var script = Script();
        Assert.Contains("New-NetFirewallRule", script);
        Assert.Matches(@"-Direction Inbound\s+-Protocol TCP\s+-LocalPort \$Port", script);
        Assert.Contains("-RemoteAddress $AdresseSecond", script);
        Assert.Contains("-Action Allow", script);
        Assert.Contains("-Profile Private", script);
        Assert.Contains("[int]   $Port = 5432", script);
        Assert.DoesNotMatch(@"-RemoteAddress\s+(Any|\*|""?LocalSubnet)", script);
        Assert.DoesNotContain("-Profile Any", script);
        Assert.DoesNotContain("-Profile Public", script);
    }

    [Fact]
    public void Nom_de_regle_fixe_et_remplacement_sans_doublon()
    {
        var script = Script();
        Assert.Contains("$NomRegle = \"PharmaOfficine PostgreSQL 5432 (2e serveur)\"", script);
        Assert.Contains("Get-NetFirewallRule -DisplayName $NomRegle", script);
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.True(script.IndexOf("Remove-NetFirewallRule", StringComparison.Ordinal) < script.IndexOf("New-NetFirewallRule", StringComparison.Ordinal),
            "L'ancienne règle doit être retirée avant la création de la nouvelle.");
    }

    [Fact]
    public void Whatif_supporte_et_aucune_action_hors_should_process()
    {
        var script = Script();
        Assert.Contains("[CmdletBinding(SupportsShouldProcess", script);
        Assert.Contains("$PSCmdlet.ShouldProcess(", script);
        Assert.Contains("[SIMULATION]", script);
        // La création et la suppression de règle sont protégées par ShouldProcess (aucune au premier niveau)
        foreach (var commande in new[] { "New-NetFirewallRule", "Remove-NetFirewallRule" })
            foreach (var index in Occurrences(script, commande))
                Assert.Contains("$PSCmdlet.ShouldProcess(", script[Math.Max(0, index - 450)..index]);
        Assert.DoesNotContain("-Force", script);
    }

    [Fact]
    public void Avertit_si_une_autre_regle_ouvre_le_port()
    {
        var script = Script();
        Assert.Contains("Get-NetFirewallPortFilter", script);
        Assert.Contains("Write-Warning", script);
        Assert.Contains("OUVERTE À TOUT LE RÉSEAU", script);
        Assert.Contains("$_.DisplayName -ne $NomRegle", script);
    }

    [Fact]
    public void Ne_touche_ni_pg_hba_ni_postgresql_conf_et_renvoie_vers_preparer_principal()
    {
        var script = Script();
        Assert.DoesNotContain("Add-Content", script);
        Assert.DoesNotContain("ALTER SYSTEM", script);
        Assert.Contains("preparer-principal.ps1", script);
        // Cohérence : le script de préparation limite pg_hba.conf au /32 du 2e serveur et gère listen_addresses
        var preparer = File.ReadAllText(Path.Combine(RacineDepot(), "scripts", "second-serveur", "preparer-principal.ps1"));
        Assert.Contains("$AdresseSecond/32", preparer);
        Assert.Contains("listen_addresses", preparer);
        Assert.DoesNotContain("0.0.0.0/0", preparer);
    }

    private static IEnumerable<int> Occurrences(string texte, string motif)
    {
        for (var i = texte.IndexOf(motif, StringComparison.Ordinal); i >= 0; i = texte.IndexOf(motif, i + 1, StringComparison.Ordinal))
            yield return i;
    }
}
