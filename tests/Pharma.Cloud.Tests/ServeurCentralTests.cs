using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Pharma.Cloud.Api.Reception;
using Pharma.Cloud.Api.Schema;
using Pharma.Cloud.Api.Securite;
using Pharma.Shared;

namespace Pharma.Cloud.Tests;

/// <summary>Racine du dépôt (variable PHARMA_RACINE_DEPOT ou recherche de PharmaOfficine.slnx).</summary>
internal static class Depot
{
    public static string Racine()
    {
        if (Environment.GetEnvironmentVariable("PHARMA_RACINE_DEPOT") is { Length: > 0 } r) return r;
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "PharmaOfficine.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("Racine du dépôt introuvable.");
    }

    public static string Schema() => File.ReadAllText(Path.Combine(Racine(), "database", "init", "schema.sql")).Replace("\r\n", "\n");

    public static Dictionary<string, ClassementTable> Classement() => GenerateurMiroir.Classement(
        Directory.GetFiles(Path.Combine(Racine(), "database", "migration"), "*.sql").Order(StringComparer.Ordinal).Select(File.ReadAllText));
}

/// <summary>Schéma de la base centrale aligné sur le schéma local (script 002 généré).</summary>
public class SchemaMiroirTests
{
    [Fact]
    public void Le_script_des_tables_miroir_est_a_jour()
    {
        var racine = Depot.Racine();
        var attendu = GenerateurMiroir.Generer(racine).Replace("\r\n", "\n");
        var actuel = File.ReadAllText(GenerateurMiroir.CheminScript(racine)).Replace("\r\n", "\n");
        Assert.True(attendu == actuel,
            "database/cloud/002_tables_miroir.sql n'est plus aligné sur le schéma local : Pharma.Cloud.Api.exe --generer-miroir <racine du dépôt>.");
    }

    [Fact]
    public void Chaque_table_envoyee_a_son_miroir_avec_les_memes_colonnes_et_le_code_officine()
    {
        var locales = GenerateurMiroir.ColonnesLocales(Depot.Schema());
        var classement = Depot.Classement();
        var tables = GenerateurMiroir.Tables(Depot.Schema(), classement);
        Assert.Equal(classement.Values.Count(GenerateurMiroir.Envoyee), tables.Count);
        foreach (var t in tables)
        {
            var c = classement[t.Nom];
            var attendues = locales[t.Nom].Select(x => x.Nom)
                .Where(n => !c.Exclues.Contains(n) && !(c.Porteur && n is "porteur_nom" or "porteur_adresse" or "porteur_piece"))
                .Concat(c.Porteur ? ["porteur_code"] : []);
            Assert.Equal(attendues, t.Colonnes.Select(x => x.Nom));
            Assert.Contains(t.Colonnes, x => x.Nom == "officine_id" && x.Type == "uuid");
        }
    }

    [Fact]
    public void Toute_table_envoyee_ou_recue_porte_le_code_officine_localement()
    {
        var locales = GenerateurMiroir.ColonnesLocales(Depot.Schema());
        var sans = Depot.Classement().Values
            .Where(c => (c.Sens != "jamais" || GenerateurMiroir.TablesOptionnelles.Contains(c.Nom)) && locales.ContainsKey(c.Nom))
            .Where(c => !locales[c.Nom].Any(x => x.Nom == "officine_id" && x.Type == "uuid"))
            .Select(c => c.Nom).ToList();
        Assert.True(sans.Count == 0, "Tables sans colonne officine_id (script 128) : " + string.Join(", ", sans));
    }

    [Fact]
    public void Aucun_nom_de_patient_ni_de_porteur_dans_la_base_centrale()
    {
        var tables = GenerateurMiroir.Tables(Depot.Schema(), Depot.Classement());
        var patient = tables.Single(t => t.Nom == "patient");
        Assert.DoesNotContain(patient.Colonnes, c => c.Nom is "nom" or "prenom" or "telephone" or "num_assure" or "date_naissance" or "email");
        Assert.Equal("text", patient.Colonnes.Single(c => c.Nom == "id").Type);
        Assert.DoesNotContain(tables.SelectMany(t => t.Colonnes), c => c.Nom is "porteur_nom" or "porteur_piece" or "porteur_adresse");
        Assert.Equal("text", tables.Single(t => t.Nom == "vente").Colonnes.Single(c => c.Nom == "patient_id").Type);
        Assert.DoesNotContain(tables, t => t.Nom is "ordonnance" or "sync_pseudonyme" or "historique_chifa");
    }
}

/// <summary>Autorité de certification, épinglage et code d'activation.</summary>
public sealed class AutoriteTests : IDisposable
{
    private readonly string dossier = Path.Combine(Path.GetTempPath(), "pharma-ac-" + Guid.NewGuid().ToString("N"));
    private const string Mdp = "mot-de-passe-de-test-assez-long";
    private static readonly DateTimeOffset Maintenant = DateTimeOffset.UtcNow;

    public void Dispose()
    {
        try { Directory.Delete(dossier, true); }
        catch (IOException) { }
    }

    private static string Demande(out ECDsa cle, ECCurve? courbe = null)
    {
        cle = ECDsa.Create(courbe ?? ECCurve.NamedCurves.nistP256);
        return new CertificateRequest("CN=demande", cle, HashAlgorithmName.SHA256).CreateSigningRequestPem();
    }

    [Fact]
    public void L_autorite_signe_une_demande_pour_3_ans_avec_le_sujet_impose()
    {
        var ac = AutoriteCertification.Creer(dossier, Mdp, Maintenant);
        var officine = Guid.NewGuid();
        using var cert = ac.SignerDemande(Demande(out var cle), officine, "principal", Maintenant);
        Assert.Contains(officine.ToString("D"), cert.Subject);
        Assert.Contains("OU=principal", cert.Subject);
        Assert.InRange((cert.NotAfter.ToUniversalTime() - Maintenant.UtcDateTime).TotalDays, 3 * 365, 3 * 365 + 2);
        Assert.True(ac.ValiderClient(cert, Maintenant));
        Assert.False(ac.ValiderClient(cert, Maintenant.AddYears(4)));
        using var complet = cert.CopyWithPrivateKey(cle);
        Assert.Equal(ac.EmpreinteRacine, AutoriteCertification.Charger(dossier, Mdp).EmpreinteRacine);
        Assert.Throws<InvalidOperationException>(() => AutoriteCertification.Creer(dossier, Mdp, Maintenant));
        Assert.Throws<InvalidOperationException>(() => AutoriteCertification.Charger(dossier, "autre-mot-de-passe-long"));
        Assert.Throws<InvalidOperationException>(() => AutoriteCertification.Creer(Path.Combine(dossier, "b"), "court", Maintenant));
    }

    [Fact]
    public void Un_certificat_d_une_autre_autorite_ou_une_demande_non_conforme_sont_refuses()
    {
        var ac = AutoriteCertification.Creer(dossier, Mdp, Maintenant);
        var autre = AutoriteCertification.Creer(Path.Combine(dossier, "autre"), Mdp, Maintenant);
        using var etranger = autre.SignerDemande(Demande(out _), Guid.NewGuid(), "principal", Maintenant);
        Assert.False(ac.ValiderClient(etranger, Maintenant));
        Assert.Throws<DemandeInvalideException>(() => ac.SignerDemande(Demande(out _, ECCurve.NamedCurves.nistP384), Guid.NewGuid(), "principal", Maintenant));
        Assert.Throws<DemandeInvalideException>(() => ac.SignerDemande("-----BEGIN CERTIFICATE REQUEST-----\nAAAA\n-----END CERTIFICATE REQUEST-----",
            Guid.NewGuid(), "principal", Maintenant));
        using var rsa = RSA.Create(2048);
        Assert.Throws<DemandeInvalideException>(() => ac.SignerDemande(
            new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem(), Guid.NewGuid(), "principal", Maintenant));
    }

    [Fact]
    public void L_autorite_refuse_un_dossier_du_depot() =>
        Assert.Throws<InvalidOperationException>(() => AutoriteCertification.Creer(Path.Combine(Depot.Racine(), "ac-interdite"), Mdp, Maintenant));

    [Fact]
    public void L_officine_n_accepte_que_le_serveur_signe_par_l_autorite_epinglee()
    {
        var ac = AutoriteCertification.Creer(dossier, Mdp, Maintenant);
        ac.CreerCertificatServeur(dossier, Mdp, ["localhost", "127.0.0.1"], Maintenant);
        using var serveur = X509CertificateLoader.LoadPkcs12FromFile(Path.Combine(dossier, AutoriteCertification.FichierServeur), Mdp);
        var autre = AutoriteCertification.Creer(Path.Combine(dossier, "autre"), Mdp, Maintenant);
        Assert.True(EpinglageServeur.Accepter(serveur, null, SslPolicyErrors.RemoteCertificateChainErrors, ac.Racine, null));
        Assert.False(EpinglageServeur.Accepter(serveur, null, SslPolicyErrors.RemoteCertificateChainErrors, autre.Racine, null));
        Assert.False(EpinglageServeur.Accepter(serveur, null, SslPolicyErrors.RemoteCertificateNameMismatch, ac.Racine, null));
        using var chaine = new X509Chain();
        chaine.ChainPolicy.ExtraStore.Add(ac.Racine);
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chaine.Build(serveur);
        Assert.True(EpinglageServeur.Accepter(serveur, chaine, SslPolicyErrors.RemoteCertificateChainErrors, null, ac.EmpreinteRacine[..32]));
        Assert.False(EpinglageServeur.Accepter(serveur, chaine, SslPolicyErrors.RemoteCertificateChainErrors, null, autre.EmpreinteRacine[..32]));
        Assert.False(EpinglageServeur.Accepter(serveur, chaine, SslPolicyErrors.None, null, null));
    }

    [Fact]
    public void Code_d_activation_lisible_epingle_et_jamais_garde_en_clair()
    {
        var empreinte = new string('a', 64);
        var code = CodeActivationSync.Generer(empreinte);
        Assert.Matches("^[A-Z2-9]{5}-[A-Z2-9]{5}-[A-Z2-9]{5}-[A-Z2-9]{5}\\.a{32}$", code);
        var lu = CodeActivationSync.Lire(" " + code.ToLowerInvariant() + " ")!.Value;
        Assert.Equal(new string('a', 32), lu.Epinglage);
        Assert.Equal(20, lu.Secret.Length);
        Assert.Matches("^[0-9a-f]{64}$", CodeActivationSync.EmpreinteSecret(lu.Secret));
        Assert.Null(CodeActivationSync.Lire(code.Split('.')[0]));
        Assert.Null(CodeActivationSync.Lire("AAAAA-AAAAA-AAAAA-AAAA0." + new string('a', 32)));
        Assert.Null(CodeActivationSync.Lire(code[..^1]));
        Assert.NotEqual(code, CodeActivationSync.Generer(empreinte));
    }
}

/// <summary>Jetons ES256 de 10 minutes liés au certificat.</summary>
public class JetonsTests
{
    private static readonly string Cert = new('a', 64);
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    [Fact]
    public async Task Un_jeton_n_est_valable_qu_avec_son_certificat_et_pendant_10_minutes()
    {
        var jetons = new JetonsCentral(ECDsa.Create(ECCurve.NamedCurves.nistP256));
        var officine = Guid.NewGuid();
        var (jeton, expire) = jetons.Emettre(officine, "principal", Cert, T0);
        Assert.Equal(T0.AddMinutes(10), expire);
        var a = await jetons.VerifierAsync(jeton, Cert, T0.AddMinutes(5));
        Assert.NotNull(a);
        Assert.Equal(officine, a!.Officine);
        Assert.Equal("principal", a.Serveur);
        Assert.Null(await jetons.VerifierAsync(jeton, new string('b', 64), T0.AddMinutes(5)));
        Assert.Null(await jetons.VerifierAsync(jeton, Cert, T0.AddMinutes(11)));
        Assert.Null(await new JetonsCentral(ECDsa.Create(ECCurve.NamedCurves.nistP256)).VerifierAsync(jeton, Cert, T0.AddMinutes(5)));
        var parties = jeton.Split('.');
        var charge = Encoding.UTF8.GetString(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parties[1]))
            .Replace(officine.ToString("D"), Guid.NewGuid().ToString("D"));
        Assert.Null(await jetons.VerifierAsync($"{parties[0]}.{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(charge)}.{parties[2]}", Cert, T0.AddMinutes(5)));
        var aucun = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}""");
        Assert.Null(await jetons.VerifierAsync($"{aucun}.{parties[1]}.", Cert, T0.AddMinutes(5)));
    }
}

/// <summary>Contrôles d'un lot déposé, avant écriture.</summary>
public class ControleLotTests
{
    private static readonly Guid Officine = Guid.NewGuid();

    private static byte[] Lot(Guid officine, Func<LotSyncDto, LotSyncDto>? alterer = null)
    {
        var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(new EnTeteLotSyncDto(ContratSync.VersionV1, officine, 1, 1, 1, ""),
            [new LigneSyncDto(1, "vente", "1", ContratSync.Creation, new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc), """{"id":1}""")]));
        return SerialisationSyncV1.Serialiser(alterer?.Invoke(lot) ?? lot);
    }

    [Fact]
    public void Un_lot_conforme_est_accepte()
    {
        var c = ControleLot.Controler(Lot(Officine), Officine);
        Assert.True(c.Valide);
        Assert.Equal(1, c.Lot!.EnTete.NumeroLot);
    }

    [Fact]
    public void Lot_d_une_autre_officine_empreinte_fausse_non_canonique_trop_grand_ou_illisible_refuses()
    {
        Assert.Equal(403, ControleLot.Controler(Lot(Guid.NewGuid()), Officine).CodeHttp);
        Assert.Equal(422, ControleLot.Controler(Lot(Officine, l => l with { EnTete = l.EnTete with { Empreinte = new string('0', 64) } }), Officine).CodeHttp);
        var espaces = Encoding.UTF8.GetBytes(" " + Encoding.UTF8.GetString(Lot(Officine)));
        Assert.Equal(422, ControleLot.Controler(espaces, Officine).CodeHttp);
        Assert.Equal(413, ControleLot.Controler(new byte[ControleLot.TailleMax + 1], Officine).CodeHttp);
        Assert.Equal(400, ControleLot.Controler("pas du json"u8, Officine).CodeHttp);
        Assert.Equal(400, ControleLot.Controler(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Lot(Officine)).Replace("sync-v1", "sync-v9")), Officine).CodeHttp);
        Assert.Equal(400, ControleLot.Controler([], Officine).CodeHttp);
    }
}
