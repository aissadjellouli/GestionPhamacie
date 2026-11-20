using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Pharma.Api.Securite;
using Pharma.Application.SuperAdmin;
using Pharma.Infrastructure.Impression;
using Pharma.Infrastructure.Notifications;
using Pharma.Infrastructure.Sauvegardes;
using Pharma.Infrastructure.Securite;
using Pharma.Infrastructure.Systeme;

namespace Pharma.Architecture.Tests;

/// <summary>
/// API utilisable sous Linux (démo cloud, étape 0) : protection des secrets locaux (DPAPI sous Windows, DataProtection ailleurs),
/// outils PostgreSQL, chemins par défaut, polices des PDF, en-têtes de proxy. Sous Windows, le comportement d'avant est gardé.
/// </summary>
public class ApiSousLinuxTests
{
    private static string DossierTemporaire() => Path.Combine(Path.GetTempPath(), "pharma-linux-" + Guid.NewGuid().ToString("N"));

    private static void Supprimer(string dossier)
    {
        if (Directory.Exists(dossier)) Directory.Delete(dossier, recursive: true);
    }

    // ---- Choix de la protection ----

    [Theory]
    [InlineData(null, true, ChoixProtection.Dpapi)]
    [InlineData("", true, ChoixProtection.Dpapi)]
    [InlineData("auto", true, ChoixProtection.Dpapi)]
    [InlineData(null, false, ChoixProtection.DataProtection)]
    [InlineData("auto", false, ChoixProtection.DataProtection)]
    [InlineData("DataProtection", true, ChoixProtection.DataProtection)]
    [InlineData("dataprotection", false, ChoixProtection.DataProtection)]
    [InlineData("dpapi", true, ChoixProtection.Dpapi)]
    public void Protection_choisie_selon_la_plateforme_et_le_reglage(string? reglage, bool windows, string attendu) =>
        Assert.Equal(attendu, ChoixProtection.Mode(reglage, windows));

    [Theory]
    [InlineData("dpapi")]
    [InlineData("inconnu")]
    public void Protection_impossible_hors_windows_refusee(string reglage) =>
        Assert.Throws<InvalidOperationException>(() => ChoixProtection.Mode(reglage, windows: false));

    [Fact]
    public void Implementation_creee_selon_le_mode_avec_dossier_de_cles_sous_la_racine()
    {
        Assert.IsType<ProtectionDpapiMachine>(ProtectionDataProtection.Choisir(new ReglagesProtection(), "/racine", windows: true));
        var dp = Assert.IsType<ProtectionDataProtection>(ProtectionDataProtection.Choisir(new ReglagesProtection(), "/racine", windows: false));
        Assert.Equal(Path.Combine("/racine", ChoixProtection.SousDossierCles), dp.DossierCles);
        var force = Assert.IsType<ProtectionDataProtection>(ProtectionDataProtection.Choisir(new ReglagesProtection("dataprotection", "/cles"), "/racine", windows: true));
        Assert.Equal("/cles", force.DossierCles);
    }

    [Fact]
    public void Dpapi_relit_les_donnees_protegees_comme_avant_et_inversement()
    {
        if (!OperatingSystem.IsWindows()) return;
        var p = new ProtectionDpapiMachine();
        var clair = RandomNumberGenerator.GetBytes(32);
        // Même entropie que l'ancien code ("PharmaOfficine.Integrite"u8) : fichiers existants relus, nouveaux fichiers lisibles par l'ancien code
        var ancien = ProtectedData.Protect(clair, "PharmaOfficine.Integrite"u8.ToArray(), DataProtectionScope.LocalMachine);
        Assert.Equal(clair, p.Deproteger(ancien, "PharmaOfficine.Integrite"));
        Assert.Equal(clair, ProtectedData.Unprotect(p.Proteger(clair, "PharmaOfficine.Integrite"), "PharmaOfficine.Integrite"u8.ToArray(), DataProtectionScope.LocalMachine));
        // Mot de passe SMTP : même entropie qu'avant
        var smtp = ProtectedData.Protect(Encoding.UTF8.GetBytes("secret-fictif"), "PharmaOfficine.Notifications.Smtp"u8.ToArray(), DataProtectionScope.LocalMachine);
        Assert.Equal("secret-fictif", new ProtectionSecretSmtp(p).Dechiffrer(smtp));
    }

    // ---- DataProtection : aller-retour ----

    [Fact]
    public void DataProtection_protege_et_relit_avec_les_cles_du_dossier()
    {
        var dossier = DossierTemporaire();
        try
        {
            var cles = Path.Combine(dossier, "cles");
            var clair = RandomNumberGenerator.GetBytes(32);
            var protege = new ProtectionDataProtection(cles).Proteger(clair, "PharmaOfficine.Integrite");
            Assert.NotEqual(clair, protege);
            Assert.True(Directory.EnumerateFiles(cles, "*.xml").Any());

            // Autre instance, même dossier (redémarrage) : relu ; autre usage ou autre dossier : illisible
            Assert.Equal(clair, new ProtectionDataProtection(cles).Deproteger(protege, "PharmaOfficine.Integrite"));
            Assert.ThrowsAny<CryptographicException>(() => new ProtectionDataProtection(cles).Deproteger(protege, "PharmaOfficine.SuperAdmin"));
            Assert.ThrowsAny<CryptographicException>(() => new ProtectionDataProtection(Path.Combine(dossier, "autres")).Deproteger(protege, "PharmaOfficine.Integrite"));
        }
        finally { Supprimer(dossier); }
    }

    [Fact]
    public void Cle_de_scellement_compte_super_admin_et_smtp_sous_DataProtection()
    {
        var dossier = DossierTemporaire();
        try
        {
            var cles = Path.Combine(dossier, "cles");
            var chemin = Path.Combine(dossier, "cle-integrite.dat");
            var message = "sceau-v1|integrite_essai|officine|manuel"u8.ToArray();
            var premier = new ScelleurIntegriteLocal(chemin, new ProtectionDataProtection(cles));
            var sceau = premier.Sceller(message);
            Assert.True(File.Exists(chemin));
            Assert.True(File.ReadAllBytes(chemin).Length > 32);
            var relu = new ScelleurIntegriteLocal(chemin, new ProtectionDataProtection(cles));
            Assert.Equal(premier.EmpreinteCle, relu.EmpreinteCle);
            Assert.True(relu.Verifier(message, sceau));
            // Clés perdues : jamais de nouvelle clé en silence
            Assert.Throws<InvalidOperationException>(() => new ScelleurIntegriteLocal(chemin, new ProtectionDataProtection(Path.Combine(dossier, "perdues"))).Sceller(message));

            var comptes = new CompteSuperAdminFichier(Path.Combine(dossier, "super-admin.dat"), new ProtectionDataProtection(cles));
            comptes.Enregistrer(new CompteSuperAdmin("admin-fictif", "hash-fictif", null, null));
            Assert.Equal("admin-fictif", new CompteSuperAdminFichier(Path.Combine(dossier, "super-admin.dat"), new ProtectionDataProtection(cles)).Lire()?.Identifiant);

            var smtp = new ProtectionSecretSmtp(new ProtectionDataProtection(cles));
            Assert.Equal("secret-fictif", smtp.Dechiffrer(smtp.Chiffrer("secret-fictif")));
            Assert.Throws<InvalidOperationException>(() => smtp.Dechiffrer([1, 2, 3]));
        }
        finally { Supprimer(dossier); }
    }

    // ---- Outils PostgreSQL ----

    private static string Creer(string fichier)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fichier)!);
        File.WriteAllText(fichier, "");
        return fichier;
    }

    [Fact]
    public void Outils_postgres_sous_windows_derniere_version_installee_avec_exe()
    {
        var dossier = DossierTemporaire();
        try
        {
            var racine = Path.Combine(dossier, "PostgreSQL");
            Creer(Path.Combine(racine, "9", "bin", "pg_restore.exe"));
            var attendu = Creer(Path.Combine(racine, "16", "bin", "pg_restore.exe"));
            Creer(Path.Combine(racine, "15", "bin", "pg_restore.exe"));
            Creer(Path.Combine(racine, "17", "bin", "pg_restore"));   // sans .exe : ignoré sous Windows
            var path = Creer(Path.Combine(dossier, "path", "pg_restore.exe"));
            Assert.Equal(attendu, OutilsPostgres.Trouver("pg_restore", null, windows: true, [racine], OutilsPostgres.DossiersPath(true, Path.GetDirectoryName(path))));
            Assert.Null(OutilsPostgres.Trouver("psql", null, windows: true, [racine], []));
        }
        finally { Supprimer(dossier); }
    }

    [Fact]
    public void Outils_postgres_reglage_prioritaire()
    {
        var dossier = DossierTemporaire();
        try
        {
            var racine = Path.Combine(dossier, "PostgreSQL");
            Creer(Path.Combine(racine, "16", "bin", "psql.exe"));
            Creer(Path.Combine(racine, "16", "bin", "psql"));
            var pgDump = Path.Combine(dossier, "regle", "pg_dump.exe");
            var exe = Creer(Path.Combine(dossier, "regle", "psql.exe"));
            var sansExe = Creer(Path.Combine(dossier, "regle", "psql"));
            Assert.Equal(exe, OutilsPostgres.Trouver("psql", pgDump, windows: true, [racine], []));
            Assert.Equal(sansExe, OutilsPostgres.Trouver("psql", pgDump, windows: false, [racine], []));
        }
        finally { Supprimer(dossier); }
    }

    [Fact]
    public void Outils_postgres_hors_windows_sans_exe_puis_dans_le_path()
    {
        var dossier = DossierTemporaire();
        try
        {
            var racine = Path.Combine(dossier, "usr-lib-postgresql");
            Creer(Path.Combine(racine, "15", "bin", "pg_dump"));
            var attendu = Creer(Path.Combine(racine, "16", "bin", "pg_dump"));
            Creer(Path.Combine(racine, "17", "bin", "pg_dump.exe"));
            Assert.Equal(attendu, OutilsPostgres.Trouver("pg_dump", null, windows: false, [racine], []));

            var bin = Path.Combine(dossier, "bin");
            var dansPath = Creer(Path.Combine(bin, "pg_restore"));
            Assert.Equal(dansPath, OutilsPostgres.Trouver("pg_restore", null, windows: false, [racine], [Path.Combine(dossier, "vide"), bin]));
            Assert.Equal("pg_dump", OutilsPostgres.NomExecutable("pg_dump", windows: false));
            Assert.Equal("pg_dump.exe", OutilsPostgres.NomExecutable("pg_dump", windows: true));
            Assert.Empty(OutilsPostgres.DossiersPath(windows: true, "C:\\x"));
            Assert.Equal(["/usr/lib/postgresql"], OutilsPostgres.RacinesVersions(windows: false));
        }
        finally { Supprimer(dossier); }
    }

    // ---- Chemins par défaut ----

    [Fact]
    public void Chemins_par_defaut_windows_identiques_a_avant()
    {
        var avant = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine");
        Assert.Equal(avant, DossiersDonnees.Racine(null, windows: true));
        Assert.Equal(avant, DossiersDonnees.Racine("  ", windows: true));
        Assert.Equal(Path.Combine(avant, "cle-integrite.dat"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PharmaOfficine", "cle-integrite.dat"));
        Assert.Equal(DossiersDonnees.RacineHorsWindows, DossiersDonnees.Racine(null, windows: false));
        Assert.Equal("/donnees", DossiersDonnees.Racine("/donnees", windows: false));
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL"),
            Assert.Single(OutilsPostgres.RacinesVersions(windows: true)));
    }

    [Theory]
    [InlineData("C:\\ProgramData\\PharmaOfficine\\GED", true, "C:\\ProgramData\\PharmaOfficine\\GED")]
    [InlineData("D:\\CHIFA_OFFICINE\\CNAS_SIGNED", true, "D:\\CHIFA_OFFICINE\\CNAS_SIGNED")]
    [InlineData("C:\\ProgramData\\PharmaOfficine\\GED", false, null)]
    [InlineData("\\\\serveur\\partage", false, null)]
    [InlineData("/var/lib/pharmaofficine/GED", false, "/var/lib/pharmaofficine/GED")]
    [InlineData("", true, null)]
    [InlineData(null, false, null)]
    public void Chemin_windows_ignore_hors_windows_seulement(string? valeur, bool windows, string? attendu) =>
        Assert.Equal(attendu, DossiersDonnees.CheminUtilisable(valeur, windows));

    // ---- Polices des PDF ----

    [Fact]
    public void Resolveur_rend_liberation_sans_dans_les_quatre_styles()
    {
        var r = new PolicesEmbarquees();
        Assert.Equal(PolicesEmbarquees.Normal, r.ResolveTypeface("Arial", false, false).FaceName);
        Assert.Equal(PolicesEmbarquees.Gras, r.ResolveTypeface("Arial", true, false).FaceName);
        Assert.Equal(PolicesEmbarquees.Italique, r.ResolveTypeface("Courier New", false, true).FaceName);
        Assert.Equal(PolicesEmbarquees.GrasItalique, r.ResolveTypeface("Arial", true, true).FaceName);
        foreach (var face in new[] { PolicesEmbarquees.Normal, PolicesEmbarquees.Gras, PolicesEmbarquees.Italique, PolicesEmbarquees.GrasItalique })
        {
            var octets = r.GetFont(face);
            Assert.NotNull(octets);
            Assert.Equal(new byte[] { 0, 1, 0, 0 }, octets![..4]);   // en-tête TrueType
        }
        Assert.Null(r.GetFont("inconnue"));
    }

    // ---- Proxy inverse ----

    private static IConfiguration Config(params (string Cle, string Valeur)[] valeurs) =>
        new ConfigurationBuilder().AddInMemoryCollection(valeurs.Select(v => new KeyValuePair<string, string?>(v.Cle, v.Valeur))).Build();

    [Fact]
    public void En_tetes_de_proxy_inactifs_sans_reglage() => Assert.Null(ProxysInverses.Options(Config()));

    [Fact]
    public void En_tetes_de_proxy_acceptes_des_seuls_proxys_declares()
    {
        var o = ProxysInverses.Options(Config(("ProxysConnus:0", "10.0.0.2"), ("ProxysConnus:1", "172.18.0.0/16")))!;
        Assert.Equal(System.Net.IPAddress.Parse("10.0.0.2"), Assert.Single(o.KnownProxies));
        Assert.Equal("172.18.0.0/16", Assert.Single(o.KnownIPNetworks).ToString());
        var unique = ProxysInverses.Options(Config(("ProxysConnus", "10.0.0.2; 10.0.0.3")))!;
        Assert.Equal(2, unique.KnownProxies.Count);
        Assert.Empty(unique.KnownIPNetworks);
        Assert.Throws<InvalidOperationException>(() => ProxysInverses.Options(Config(("ProxysConnus", "pas-une-ip"))));
    }
}

/// <summary>
/// PDF généré avec les polices embarquées forcées (comme hors Windows). Réglage global de PDFsharp : collection exécutée seule,
/// réglage d'origine remis à la fin.
/// </summary>
[CollectionDefinition("Polices PDF", DisableParallelization = true)]
public class CollectionPolicesPdf;

[Collection("Polices PDF")]
public class PolicesPdfForceesTests
{
    [Fact]
    public void Pdf_genere_avec_les_polices_embarquees()
    {
        var documents = new DocumentsPdf();   // réglage d'origine posé d'abord (constructeur statique)
        try
        {
            PdfSharp.Fonts.GlobalFontSettings.ResetFontManagement();
            PdfSharp.Fonts.GlobalFontSettings.FontResolver = new PolicesEmbarquees();
            var ticket = new Pharma.Application.Caisse.TicketVente("T-000001", DateTime.UtcNow, "Dr Titulaire Fictif", "Caisse 1", "validee", null, null,
                [new Pharma.Application.Caisse.LigneTicket("Produit fictif 500 mg comprimés", 1, 100, 100, 0)],
                100m, 0, 0, 100m, "especes", "Pharmacie Fictive", "1 rue Fictive", "000", "123");
            var pdf = documents.Ticket(ticket, 20m, 80);
            Assert.StartsWith("%PDF", Encoding.ASCII.GetString(pdf, 0, 4));
            var texte = Encoding.ASCII.GetString(pdf);
            var polices = System.Text.RegularExpressions.Regex.Matches(texte, @"/BaseFont/([^/\s]+)").Select(m => m.Groups[1].Value).Distinct().ToList();
            Assert.NotEmpty(polices);
            Assert.All(polices, p => Assert.Contains("Liberation#20Sans", p));   // « Liberation Sans » embarquée, aucune police du système
        }
        finally
        {
            PdfSharp.Fonts.GlobalFontSettings.ResetFontManagement();
            if (OperatingSystem.IsWindows()) PdfSharp.Fonts.GlobalFontSettings.UseWindowsFontsUnderWindows = true;
            else PdfSharp.Fonts.GlobalFontSettings.FontResolver = new PolicesEmbarquees();
        }
    }
}
