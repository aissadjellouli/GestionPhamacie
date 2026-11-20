using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using ZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;
using Pharma.Cloud.Api.Depart;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Archive de départ d'une officine en zip chiffré AES-256 (WinZip AE-2), règle du 2026-11-20 : chaque entrée chiffrée, illisible
/// sans le mot de passe, contenu identique à l'ancien zip non chiffré ; mot de passe tiré au sort (24 caractères, différent à chaque
/// tirage), affiché une seule fois et absent du journal des départs. Sans base : l'export complet est vérifié par DepartOfficineTests.
/// </summary>
public class DepartArchiveChiffreeTests
{
    private static readonly (string Nom, string Texte)[] Contenu =
    [
        ("ligne_remplacee.csv", "officine_id;id;donnees\naaaa;1;première version\n"),
        ("produit.csv", "officine_id;id;nom_commercial\naaaa;40;\"Périmé ; \"\"lot\"\"\nà retirer\"\n" + string.Concat(Enumerable.Repeat("aaaa;1;MARQUEUR-EN-CLAIR\n", 2000))),
        ("vide.csv", ""),
        (DepartOfficine.FichierEmpreintes, "aa  produit.csv\n"),
    ];

    [Fact]
    public async Task Chaque_entree_est_chiffree_AES_256_AE_2_illisible_sans_mot_de_passe()
    {
        var mdp = MotDePasseArchive.Tirer();
        var octets = await EcrireChiffreeAsync(mdp);

        // Lu sans mot de passe : toutes les entrées sont chiffrées en AES-256 et leur contenu est refusé
        using (var zip = new ZipFile(new MemoryStream(octets)))
        {
            var entrees = zip.Cast<ZipEntry>().ToList();
            Assert.Equal(Contenu.Select(c => c.Nom), entrees.Select(e => e.Name));
            Assert.All(entrees, e => Assert.True(e.IsCrypted && e.AESKeySize == 256, e.Name));
            Assert.All(entrees, e => Assert.ThrowsAny<Exception>(() => Lire(zip, e)));
        }
        // Mauvais mot de passe : refusé
        using (var zip = new ZipFile(new MemoryStream(octets)) { Password = MotDePasseArchive.Tirer().Valeur })
            Assert.ThrowsAny<Exception>(() => Lire(zip, zip.GetEntry("produit.csv")));
        // La bibliothèque standard ne sait pas le lire non plus
        using (var standard = new ZipArchive(new MemoryStream(octets), ZipArchiveMode.Read))
            Assert.ThrowsAny<Exception>(() => standard.GetEntry("produit.csv")!.Open().ReadByte());
        // Aucun texte en clair dans le fichier
        Assert.Equal(-1, octets.AsSpan().IndexOf("MARQUEUR-EN-CLAIR"u8));
        // Format WinZip AE-2 : champ 0x9901 (taille 7, version 2, « AE », force 3 = 256 bits, Deflate) dans l'en-tête local et central
        byte[] ae2 = [0x01, 0x99, 0x07, 0x00, 0x02, 0x00, (byte)'A', (byte)'E', 0x03, 0x08, 0x00];
        Assert.Equal(Contenu.Length * 2, Occurrences(octets, ae2));
    }

    [Fact]
    public async Task Lu_avec_le_mot_de_passe_le_contenu_est_identique_a_l_ancien_zip()
    {
        var mdp = MotDePasseArchive.Tirer();
        var chiffree = await EcrireChiffreeAsync(mdp);

        // Ancien format (bibliothèque standard, non chiffré) écrit avec les mêmes contenus
        var ancien = new MemoryStream();
        using (var zip = new ZipArchive(ancien, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (nom, texte) in Contenu)
            {
                using var sortie = zip.CreateEntry(nom, CompressionLevel.Optimal).Open();
                sortie.Write(new UTF8Encoding(false).GetBytes(texte));
            }
        ancien.Position = 0;
        using var lecture = new ZipArchive(ancien, ZipArchiveMode.Read);

        using var nouveau = new ZipFile(new MemoryStream(chiffree)) { Password = mdp.Valeur };
        Assert.Equal(lecture.Entries.Select(e => e.FullName), nouveau.Cast<ZipEntry>().Select(e => e.Name));
        foreach (var e in lecture.Entries)
        {
            using var flux = e.Open();
            using var copie = new MemoryStream();
            flux.CopyTo(copie);
            Assert.Equal(copie.ToArray(), Lire(nouveau, nouveau.GetEntry(e.FullName)));
        }
    }

    [Fact]
    public async Task Archive_ecrite_dans_un_fichier_sans_copie_en_clair_et_empreinte_sur_le_fichier_chiffre()
    {
        var dossier = Directory.CreateTempSubdirectory("depart-chiffre-");
        try
        {
            var chemin = Path.Combine(dossier.FullName, "officine.zip");
            var mdp = MotDePasseArchive.Tirer();
            await using (var flux = new FileStream(chemin, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await EcrireAsync(flux, mdp);
            Assert.Equal([chemin], Directory.GetFiles(dossier.FullName));   // un seul fichier, chiffré
            var octets = File.ReadAllBytes(chemin);
            Assert.Equal(-1, octets.AsSpan().IndexOf("MARQUEUR-EN-CLAIR"u8));
            // L'empreinte remise est celle du fichier chiffré : même calcul que DepartOfficine.ExporterAsync, contrôlable par Get-FileHash
            await using var lu = new FileStream(chemin, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(octets)), Convert.ToHexStringLower(await SHA256.HashDataAsync(lu)));
        }
        finally { dossier.Delete(true); }
    }

    [Fact]
    public void Mot_de_passe_tire_au_sort_24_caracteres_sans_ambiguite_different_a_chaque_appel()
    {
        var tirages = Enumerable.Range(0, 200).Select(_ => MotDePasseArchive.Tirer().Valeur).ToList();
        Assert.All(tirages, m =>
        {
            Assert.True(m.Length >= 24);
            Assert.All(m, c => Assert.Contains(c, MotDePasseArchive.Alphabet));
        });
        Assert.Equal(tirages.Count, tirages.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(MotDePasseArchive.Alphabet, c => "0O1Il".Contains(c));
    }

    [Fact]
    public void Mot_de_passe_absent_du_journal_affiche_une_seule_fois()
    {
        var mdp = MotDePasseArchive.Tirer();
        var export = new ResultatExport(@"C:\sortie\officine.zip", new string('c', 64), [new FichierExporte("produit.csv", 2, new string('a', 64))], mdp);

        // Journal des départs (détail jsonb) : empreinte de l'archive chiffrée, jamais le mot de passe
        var journal = DepartOfficine.DetailExport(export);
        Assert.DoesNotContain(mdp.Valeur, journal);
        Assert.Contains(new string('c', 64), journal);
        Assert.Contains("AES-256", journal);
        // Un résultat affiché ou journalisé par erreur ne le révèle pas
        Assert.DoesNotContain(mdp.Valeur, export.ToString());
        Assert.DoesNotContain(mdp.Valeur, mdp.ToString());

        // Journal factice à la place de la console : le mot de passe y apparaît exactement une fois, avec l'empreinte
        using var console = new StringWriter();
        DepartOfficine.EcrireCompteRendu(console, export);
        var texte = console.ToString();
        Assert.Equal(1, Occurrences(Encoding.UTF8.GetBytes(texte), Encoding.UTF8.GetBytes(mdp.Valeur)));
        Assert.Contains(new string('c', 64), texte);
    }

    private static async Task<byte[]> EcrireChiffreeAsync(MotDePasseArchive mdp)
    {
        using var flux = new MemoryStream();
        await EcrireAsync(flux, mdp);
        return flux.ToArray();
    }

    private static async Task EcrireAsync(Stream flux, MotDePasseArchive mdp)
    {
        var zip = new ArchiveChiffree(flux, mdp);
        foreach (var (nom, texte) in Contenu)
            await zip.AjouterAsync(nom, async sortie =>
            {
                await sortie.WriteAsync(new UTF8Encoding(false).GetBytes(texte));
                return true;
            }, CancellationToken.None);
        await zip.TerminerAsync(CancellationToken.None);
    }

    internal static byte[] Lire(ZipFile zip, ZipEntry entree)
    {
        using var flux = zip.GetInputStream(entree);
        using var copie = new MemoryStream();
        flux.CopyTo(copie);
        return copie.ToArray();
    }

    private static int Occurrences(byte[] texte, byte[] motif)
    {
        var n = 0;
        for (var i = texte.AsSpan().IndexOf(motif); i >= 0;)
        {
            n++;
            var suite = texte.AsSpan(i + 1).IndexOf(motif);
            i = suite < 0 ? -1 : i + 1 + suite;
        }
        return n;
    }
}
