using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Pharma.Shared;

namespace Pharma.Architecture.Tests;

/// <summary>
/// Liaison d'un poste avec le serveur : classement des erreurs (injoignable, certificat refusé, pas basculé) sur de vraies
/// connexions locales (jamais de contournement de la validation du certificat), et contrôle des adresses d'API saisies.
/// </summary>
public class LiaisonServeurTests
{
    private static X509Certificate2 CertificatAutoSigneNonApprouve()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        req.CertificateExtensions.Add(san.Build());
        using var c = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(2));
        const string phrase = "essai-en-memoire";
        return X509CertificateLoader.LoadPkcs12(c.Export(X509ContentType.Pfx, phrase), phrase, X509KeyStorageFlags.Exportable);
    }

    private static int PortLibre()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task Certificat_non_approuve_donne_CertificatRefuse()
    {
        using var certificat = CertificatAutoSigneNonApprouve();
        var ecouteur = new TcpListener(IPAddress.Loopback, 0);
        ecouteur.Start();
        try
        {
            var port = ((IPEndPoint)ecouteur.LocalEndpoint).Port;
            var cote = Task.Run(async () =>
            {
                using var tcp = await ecouteur.AcceptTcpClientAsync();
                using var tls = new SslStream(tcp.GetStream());
                try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificat }); }
                catch (Exception ex) when (ex is AuthenticationException or IOException) { /* le poste a refusé le certificat */ }
            });

            // Validation par défaut de Windows : l'autorité de ce certificat n'est approuvée nulle part
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{port}/api/sante"));
            await cote;

            Assert.Equal(EtatLiaison.CertificatRefuse, LiaisonServeur.Classer(ex));
        }
        finally { ecouteur.Stop(); }
    }

    [Fact]
    public async Task Port_ferme_donne_Injoignable()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var ex = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync($"https://127.0.0.1:{PortLibre()}/api/sante"));
        Assert.Equal(EtatLiaison.Injoignable, LiaisonServeur.Classer(ex));
    }

    [Fact]
    public void Delai_depasse_ou_exception_inconnue_donne_Injoignable()
    {
        Assert.Equal(EtatLiaison.Injoignable, LiaisonServeur.Classer(new TaskCanceledException()));
        Assert.Equal(EtatLiaison.Injoignable, LiaisonServeur.Classer(new HttpRequestException("x", new IOException())));
        Assert.Equal(EtatLiaison.Injoignable, LiaisonServeur.Classer((Exception?)null));
    }

    [Fact]
    public void AuthenticationException_imbriquee_ou_erreur_de_connexion_securisee_donne_CertificatRefuse()
    {
        Assert.Equal(EtatLiaison.CertificatRefuse,
            LiaisonServeur.Classer(new HttpRequestException("x", new AuthenticationException("certificat"))));
        Assert.Equal(EtatLiaison.CertificatRefuse,
            LiaisonServeur.Classer(new AggregateException(new HttpRequestException("x", new IOException("y", new AuthenticationException())))));
        Assert.Equal(EtatLiaison.CertificatRefuse,
            LiaisonServeur.Classer(new HttpRequestException(HttpRequestError.SecureConnectionError, "x")));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, EtatLiaison.Joignable)]
    [InlineData(HttpStatusCode.NoContent, EtatLiaison.Joignable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, EtatLiaison.PasBascule)]
    [InlineData(HttpStatusCode.InternalServerError, EtatLiaison.PasBascule)]
    [InlineData(HttpStatusCode.NotFound, EtatLiaison.PasBascule)]
    public void Reponse_du_serveur_joignable(HttpStatusCode code, EtatLiaison attendu) =>
        Assert.Equal(attendu, LiaisonServeur.Classer(code));

    [Fact]
    public void Messages_distincts_et_utiles_pour_chaque_cas()
    {
        Assert.Null(LiaisonServeur.MessageSecours(EtatLiaison.Joignable));
        var injoignable = LiaisonServeur.MessageSecours(EtatLiaison.Injoignable)!;
        var certificat = LiaisonServeur.MessageSecours(EtatLiaison.CertificatRefuse)!;
        var pasBascule = LiaisonServeur.MessageSecours(EtatLiaison.PasBascule)!;
        Assert.Equal(3, new[] { injoignable, certificat, pasBascule }.Distinct().Count());
        Assert.Contains("autorité de la pharmacie", certificat);
        Assert.Contains("5.4", certificat);
        Assert.Contains("adresse", certificat);
        Assert.Contains("pas encore basculé", pasBascule);
        Assert.DoesNotContain("pas encore basculé", certificat);
        Assert.DoesNotContain("pas encore basculé", injoignable);
        Assert.DoesNotContain("certificat", injoignable);
        // Bandeau du serveur principal : consigne propre au certificat, rien pour les autres cas
        Assert.Contains("autorité de la pharmacie", LiaisonServeur.MessagePrincipal(EtatLiaison.CertificatRefuse)!);
        Assert.Null(LiaisonServeur.MessagePrincipal(EtatLiaison.Injoignable));
    }

    [Fact]
    public void Ligne_de_journal_sans_adresse_ni_donnee_personnelle()
    {
        var ligne = LiaisonServeur.LigneJournal("2e serveur", EtatLiaison.CertificatRefuse, new AuthenticationException("https://192.168.1.30:5443/ jean@exemple.dz"));
        Assert.Equal("Liaison 2e serveur : CertificatRefuse (AuthenticationException)", ligne);
    }

    // ----- Adresses d'API saisies -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("https://192.168.1.20:5443/")]
    [InlineData("https://PC-SECOURS:5443")]
    [InlineData("http://localhost:5080/")]
    [InlineData("http://192.168.1.20:5080/")]   // valide : seul l'avertissement s'applique, rien n'est bloqué
    public void Adresses_acceptees(string? adresse) => Assert.Null(AdresseApiPoste.Controler(adresse));

    [Theory]
    [InlineData("PC-SECOURS")]
    [InlineData("192.168.1.20:5443")]
    [InlineData("ftp://serveur/")]
    [InlineData("//serveur/")]
    public void Adresses_refusees_avec_exemple_https_5443(string adresse)
    {
        var message = AdresseApiPoste.Controler(adresse)!;
        Assert.Contains("https://<serveur>:5443/", message);
        Assert.Contains("http://localhost:5080/", message);
        Assert.Contains("PC serveur", message);
        Assert.DoesNotContain("192.168.1.20:5080", message);
    }

    [Theory]
    [InlineData("http://192.168.1.20:5080/")]
    [InlineData("http://PC-SECOURS:5080")]
    [InlineData(" http://serveur.local:5080/ ")]
    public void Http_hors_du_pc_serveur_avertit_sans_bloquer(string adresse)
    {
        Assert.Null(AdresseApiPoste.Controler(adresse));
        var avertissement = AdresseApiPoste.Avertir(adresse)!;
        Assert.Contains("https://<serveur>:5443/", avertissement);
        Assert.Contains("http://localhost:5080/", avertissement);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://192.168.1.20:5443/")]
    [InlineData("http://localhost:5080/")]
    [InlineData("http://127.0.0.1:5080/")]
    [InlineData("http://[::1]:5080/")]
    [InlineData("pas une adresse")]
    public void Pas_d_avertissement_pour_https_ou_http_local_ou_adresse_invalide(string? adresse) =>
        Assert.Null(AdresseApiPoste.Avertir(adresse));
}
