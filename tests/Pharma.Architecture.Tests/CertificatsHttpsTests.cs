using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Une seule autorité de certification par pharmacie (principal, 2e serveur, PC de secours) : contenu du script
/// scripts/certificat_https.ps1 et essai réel de la chaîne de certificats, en mémoire (aucun magasin Windows touché).
/// </summary>
public class CertificatsHttpsTests
{
    private static string Script() => File.ReadAllText(Path.Combine(RacineDepot(), "scripts", "certificat_https.ps1"));

    private static string RacineDepot()
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx")))
            dossier = dossier.Parent;
        return dossier?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    private static string BlocParam(string script)
    {
        var debut = script.IndexOf("\nparam(", StringComparison.Ordinal);
        var fin = script.IndexOf("\n)\n", debut, StringComparison.Ordinal);
        Assert.True(debut > 0 && fin > debut, "Bloc param introuvable");
        return script[debut..fin];
    }

    // ----- Contenu du script -----

    [Fact]
    public void Quatre_modes_exclusifs_par_jeux_de_parametres()
    {
        var script = Script();
        Assert.Contains("DefaultParameterSetName = \"Principal\"", script);
        var param = BlocParam(script);
        foreach (var jeu in new[] { "Principal", "AutrePc", "Importer", "Restaurer" })
            Assert.Contains($"ParameterSetName = \"{jeu}\"", param);
        // Un interrupteur obligatoire par mode non principal : les modes ne se combinent pas
        Assert.Matches(@"Mandatory, ParameterSetName = ""AutrePc""\)\] \[switch\] \$CertificatPourAutrePc", param);
        Assert.Matches(@"Mandatory, ParameterSetName = ""Importer""\)\] \[switch\] \$Importer", param);
        Assert.Matches(@"Mandatory, ParameterSetName = ""Restaurer""\)\] \[switch\] \$RestaurerAutorite", param);
        // Le certificat de l'autre PC n'écrit ni configuration ni pare-feu : ces paramètres ne lui appartiennent pas
        Assert.DoesNotMatch(@"ParameterSetName = ""AutrePc""\)\][^\[]*\[switch\] \$PareFeu", param);
    }

    [Fact]
    public void Aucune_phrase_de_passe_en_parametre_ni_en_clair()
    {
        var script = Script();
        var param = BlocParam(script);
        Assert.DoesNotMatch(@"(?i)\$\w*(phrase|passe|password|motdepasse|secret)\w*\s*[=,\r\n]", param.Replace("FichierSauvegardeAutorite", ""));
        Assert.DoesNotContain("-AsPlainText", script);
        Assert.DoesNotContain("ConvertTo-SecureString", script);
        // Toute saisie de phrase se fait à l'écran, en SecureString
        var lectures = script.Split('\n').Where(l => l.Contains("Read-Host") && !l.TrimStart().StartsWith('#')).ToList();
        Assert.NotEmpty(lectures);
        Assert.All(lectures, l => Assert.Contains("-AsSecureString", l));
        // Jamais écrite dans un journal ni affichée
        Assert.DoesNotMatch(@"(?i)(Write-Host|Write-Output|Write-Verbose|Write-Debug|Out-File|Set-Content|Add-Content)[^\r\n]*\$(phrase|p1|p2|phraseInterne)\b", script);
    }

    [Fact]
    public void Aucun_export_de_pfx_sans_phrase_de_passe_et_cles_non_exportables_dans_le_magasin()
    {
        var script = Script();
        foreach (Match m in Regex.Matches(script, @"Export-PfxCertificate[^\r\n]*"))
            Assert.Contains("-Password", m.Value);
        // Les exports .NET en PFX passent toujours la phrase (surcharge à deux arguments)
        Assert.DoesNotMatch(@"Export\(\[[\w\.]*X509ContentType\]::Pfx\)", script);
        Assert.Matches(@"Export\(\[System\.Security\.Cryptography\.X509Certificates\.X509ContentType\]::Pfx, \$\w+\)", script);
        // Import dans le magasin : jamais le drapeau Exportable, jamais -KeyExportPolicy Exportable
        Assert.DoesNotMatch(@"KeyStorageFlags\]::Exportable", script);
        Assert.DoesNotMatch(@"-KeyExportPolicy\s+Exportable", script);
        Assert.Contains("PersistKeySet", script);
        Assert.Contains("MachineKeySet", script);
    }

    [Fact]
    public void Configuration_http_localhost_et_https_reseau_dans_tous_les_modes_qui_l_ecrivent()
    {
        var script = Script();
        Assert.Contains("Url = \"http://localhost:$PortHttp\"", script);
        Assert.Contains("Url         = \"https://0.0.0.0:$PortHttps\"", script);
        Assert.DoesNotContain("http://0.0.0.0", script);
        Assert.DoesNotContain("AutoriserHttpReseau", script);
        // Mode importer : même écriture et même règle de pare-feu que le principal (une seule fonction)
        Assert.Equal(1, Regex.Matches(script, @"Url\s+= ""https://0\.0\.0\.0").Count);
        var importer = script[script.IndexOf("# Mode -Importer :", StringComparison.Ordinal)..script.IndexOf("# Mode principal (par défaut)", StringComparison.Ordinal)];
        Assert.Contains("Write-ConfigurationApi", importer);
        Assert.Contains("Set-ReglePareFeu", importer);
        // Le 2e serveur et le PC de secours ne créent jamais d'autorité
        Assert.DoesNotContain("New-CertificatAutorite", importer);
        // Pare-feu : réseau privé seulement, port https seul
        Assert.Contains("-Profile Private", script);
        Assert.DoesNotMatch(@"New-NetFirewallRule[^\r\n]*\$PortHttp\b", script);
    }

    [Fact]
    public void Ancienne_autorite_par_pc_detectee_signalee_jamais_supprimee_automatiquement()
    {
        var script = Script();
        Assert.Contains("CN=PharmaOfficine Autorite locale", script);
        Assert.Contains("Show-AncienneAutorite", script);
        Assert.Contains("n'est PAS supprimée", script);
        // Aucune suppression de certificat n'est faite sur le sujet de l'ancienne autorité
        Assert.DoesNotMatch(@"Where-Object[^\r\n]*\$sujetAutoriteAncienne[^\r\n]*\|\s*Remove-Item", script.Replace("'$sujetAutoriteAncienne' | Remove-Item", ""));
        Assert.Contains("-WhatIf", script);
        Assert.Contains("SupportsShouldProcess", script);
    }

    // ----- Essai réel de la chaîne : mêmes profils de certificats que le script, en mémoire -----

    private static X509Certificate2 CreerAutorite()
    {
        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest("CN=PharmaOfficine Autorite pharmacie", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
    }

    private static X509Certificate2 CreerServeur(X509Certificate2 autorite, params string[] noms)
    {
        var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=PharmaOfficine Mobile", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        foreach (var n in noms)
            if (IPAddress.TryParse(n, out var ip)) san.AddIpAddress(ip); else san.AddDnsName(n);
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        var serie = RandomNumberGenerator.GetBytes(16);
        serie[0] = (byte)((serie[0] & 0x7F) | 0x01);
        using var signe = req.Create(autorite, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(825), serie);
        return signe.CopyWithPrivateKey(rsa);
    }

    private static bool ChaineValide(X509Certificate2 certificat, X509Certificate2 racine, out X509ChainStatusFlags statut, out int elements)
    {
        using var chaine = new X509Chain();
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chaine.ChainPolicy.CustomTrustStore.Add(racine);
        var ok = chaine.Build(certificat);
        statut = chaine.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (a, s) => a | s.Status);
        elements = chaine.ChainElements.Count;
        return ok;
    }

    [Fact]
    public void Certificat_signe_par_l_autorite_exportee_puis_reimportee_est_accepte_avec_les_bons_noms()
    {
        const string phrase = "phrase-d-essai-uniquement-en-memoire";
        using var autorite = CreerAutorite();

        // Sauvegarde du coffre : PFX protégé par phrase de passe, puis restauration sur un autre PC
        var pfx = autorite.Export(X509ContentType.Pfx, phrase);
        using var restauree = X509CertificateLoader.LoadPkcs12(pfx, phrase, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(restauree.HasPrivateKey);
        Assert.Equal(autorite.Thumbprint, restauree.Thumbprint);
        Assert.Throws<CryptographicException>(() => X509CertificateLoader.LoadPkcs12(pfx, "mauvaise-phrase", X509KeyStorageFlags.EphemeralKeySet));

        // L'autorité restaurée signe le certificat du PC de secours (noms et adresses donnés, rien de deviné)
        using var secours = CreerServeur(restauree, "localhost", "127.0.0.1", "SECOURS", "192.168.1.30");
        Assert.True(ChaineValide(secours, autorite, out var statut, out var elements), $"Chaîne refusée : {statut}");
        Assert.Equal(2, elements);

        var sanExt = secours.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Contains("SECOURS", sanExt.EnumerateDnsNames());
        Assert.Contains("localhost", sanExt.EnumerateDnsNames());
        Assert.Contains(IPAddress.Parse("192.168.1.30"), sanExt.EnumerateIPAddresses());
        Assert.DoesNotContain("AUTRE-PC", sanExt.EnumerateDnsNames());
        Assert.Equal(autorite.Subject, secours.Issuer);
        Assert.InRange((secours.NotAfter - DateTime.Now).TotalDays, 824, 826);

        // Le certificat exporté en PFX pour la clé USB se réimporte avec sa clé et reste valide pour les postes
        var pfxSecours = secours.Export(X509ContentType.Pfx, phrase);
        using var importe = X509CertificateLoader.LoadPkcs12(pfxSecours, phrase, X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(importe.HasPrivateKey);
        Assert.True(ChaineValide(importe, autorite, out _, out _));

        // Un poste qui ne connaît que l'ANCIENNE autorité « par PC » refuse le certificat : c'est ce que la conception évite
        using var autreAutorite = CreerAutorite();
        Assert.False(ChaineValide(importe, autreAutorite, out var refus, out _));
        Assert.NotEqual(X509ChainStatusFlags.NoError, refus);
    }

    [Fact]
    public async Task Connexion_tls_reelle_acceptee_pour_le_nom_du_pc_de_secours_et_refusee_pour_un_nom_absent()
    {
        const string phrase = "phrase-d-essai-uniquement-en-memoire";
        using var autorite = CreerAutorite();
        using var serveur = CreerServeur(autorite, "localhost", "127.0.0.1", "SECOURS", "192.168.1.30");
        // Rechargé avec sa clé (comme Kestrel depuis le magasin) ; SChannel exige une clé non éphémère
        using var certificatServeur = X509CertificateLoader.LoadPkcs12(serveur.Export(X509ContentType.Pfx, phrase), phrase, X509KeyStorageFlags.Exportable);

        async Task<SslPolicyErrors> Essayer(string nomAttendu)
        {
            var ecouteur = new TcpListener(IPAddress.Loopback, 0);
            ecouteur.Start();
            try
            {
                var port = ((IPEndPoint)ecouteur.LocalEndpoint).Port;
                var cote = Task.Run(async () =>
                {
                    using var tcp = await ecouteur.AcceptTcpClientAsync();
                    using var tls = new SslStream(tcp.GetStream());
                    try
                    {
                        await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificatServeur, EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13 });
                    }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { /* le client a refusé */ }
                });
                SslPolicyErrors erreurs = SslPolicyErrors.None;
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, port);
                using var flux = new SslStream(client.GetStream(), false, (_, cert, chaine, errs) =>
                {
                    // Même décision qu'un poste qui fait confiance à l'autorité de la pharmacie : racine personnalisée, nom contrôlé
                    using var c = new X509Chain();
                    c.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                    c.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                    c.ChainPolicy.CustomTrustStore.Add(autorite);
                    var ok = c.Build(new X509Certificate2(cert!));
                    erreurs = (ok ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors) | (errs & SslPolicyErrors.RemoteCertificateNameMismatch);
                    return erreurs == SslPolicyErrors.None;
                });
                try { await flux.AuthenticateAsClientAsync(nomAttendu); } catch (AuthenticationException) { /* refus attendu pour un nom absent */ }
                await cote;
                return erreurs;
            }
            finally { ecouteur.Stop(); }
        }

        Assert.Equal(SslPolicyErrors.None, await Essayer("SECOURS"));
        Assert.Equal(SslPolicyErrors.None, await Essayer("192.168.1.30"));
        Assert.Equal(SslPolicyErrors.RemoteCertificateNameMismatch, await Essayer("INCONNU"));
    }
}
