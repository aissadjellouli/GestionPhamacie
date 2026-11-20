using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Pharma.Cloud.Api.Depart;
using Pharma.Cloud.Api.Reception;
using Pharma.Shared;
using BaseCentraleJetable = Pharma.Cloud.Tests.DescendantsMutuV1Tests.BaseCentraleJetable;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Lots S6.1 et S6.3 (scripts centraux 020 et 021, effacement complet du script 027) : maximums lus par la route GET /api/v1/maximums, export complet au départ d'une
/// officine (contenu, empreintes) et refus de l'effacement avant la fin de la durée de conservation. Base centrale JETABLE créée avec
/// les scripts database/cloud, supprimée à la fin ; sans serveur PostgreSQL joignable, sans psql ou sans droit CREATEDB, les tests de
/// base ne font rien (comme DescendantsMutuV1Tests). SQL complet : database/cloud/tests/020 et 021.
/// </summary>
public class DepartOfficineTests
{
    private static readonly Guid A = Guid.Parse("aaaaaaaa-0000-0000-0000-0000000000d1");
    private static readonly Guid B = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000d1");

    [Fact]
    public void Empreintes_une_ligne_par_fichier_triee_au_format_sha256sum()
    {
        var contenu = DepartOfficine.ContenuEmpreintes([new("vente.csv", 2, "bb"), new("alerte.csv", 1, "aa")]);
        Assert.Equal("aa  alerte.csv\nbb  vente.csv\n", contenu);
    }

    [Fact]
    public void Declaration_epoque_refusee_hors_bornes()
    {
        var ok = new DeclarationEpoqueDto(1, 2, 0, null, null, DateTimeOffset.UtcNow, null, []);
        Assert.Null(ok.Refus());
        Assert.NotNull((ok with { NouvelleEpoque = 1 }).Refus());
        Assert.NotNull((ok with { DernierLotLocal = -1 }).Refus());
        Assert.NotNull((ok with { FinsChaine = [new FinChaineDto("Journal-Vente", "officine", 1, null, null)] }).Refus());
        Assert.NotNull((ok with { FinsChaine = [new FinChaineDto("journal_vente", "officine", 1, null, "zz")] }).Refus());
        Assert.Null((ok with { FinsChaine = [new FinChaineDto("journal_vente", "officine", 1, "12", new string('a', 64))] }).Refus());
    }

    [Fact]
    public void Chemin_de_sortie_controle_jamais_ecrase()
    {
        var dossier = Directory.CreateTempSubdirectory("depart-officine-");
        try
        {
            Assert.Throws<ArgumentException>(() => DepartOfficine.VerifierChemin(null));
            Assert.Throws<ArgumentException>(() => DepartOfficine.VerifierChemin(Path.Combine(dossier.FullName, "export.txt")));
            Assert.Throws<ArgumentException>(() => DepartOfficine.VerifierChemin(Path.Combine(dossier.FullName, "absent", "export.zip")));
            var existant = Path.Combine(dossier.FullName, "export.zip");
            File.WriteAllText(existant, "x");
            Assert.Throws<ArgumentException>(() => DepartOfficine.VerifierChemin(existant));
            Assert.Equal(Path.Combine(dossier.FullName, "neuf.zip"), DepartOfficine.VerifierChemin(Path.Combine(dossier.FullName, "neuf.zip")));
        }
        finally { dossier.Delete(true); }
    }

    [Fact]
    public async Task Maximums_export_complet_et_effacement_refuse_avant_le_delai()
    {
        using var base_ = BaseCentraleJetable.Creer();
        if (base_ is null) return;   // pas de serveur, de psql ou de droit CREATEDB : rien à vérifier ici
        await using var db = NpgsqlDataSource.Create(base_.Connexion);
        var ct = CancellationToken.None;
        await Executer(db, "INSERT INTO officine (id, libelle) VALUES ($1, 'Essai départ A'), ($2, 'Essai départ B')", ct, A, B);
        await Executer(db, "INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance) VALUES ($1, $2, 'principal', 'S1', now() + interval '1 year')",
            ct, new string('e', 64), A);
        // Officine A : deux produits (texte avec « ; », guillemet, retour à la ligne et accents), puis l'id 1 repris par l'époque 2 (archivé)
        await Lot(db, A, 1, 1, """{"lignes":[{"n":1,"table":"produit","id":"1","op":"creation","donnees":{"id":1,"nom_commercial":"première version"}},{"n":2,"table":"produit","id":"40","op":"creation","donnees":{"id":40,"nom_commercial":"Périmé ; \"lot\"\nà retirer"}}]}""", ct);
        await Lot(db, A, 2, 1, """{"lignes":[{"n":1,"table":"produit","id":"1","op":"creation","donnees":{"id":1,"nom_commercial":"nouvelle"}}]}""", ct);
        await Lot(db, B, 1, 1, """{"lignes":[{"n":1,"table":"produit","id":"900","op":"creation","donnees":{"id":900,"nom_commercial":"B"}}]}""", ct);

        // Route maximums : réservée à l'officine appelante
        var depot = new DepotCentral(db);
        Assert.Equal(40, (await depot.MaximumsAsync(A, ct))["produit"]);
        Assert.Equal(900, (await depot.MaximumsAsync(B, ct))["produit"]);

        // Lot S6.5.3 (script 022) : déclaration d'époque ; renvoi identique accepté, autre contenu pour la même époque refusé (23505)
        var decl = new DeclarationEpoqueDto(2, 3, 1, 1, null, new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero), 7,
            [new FinChaineDto("journal_vente", "officine", 0, null, null)]);
        Assert.Null(decl.Refus());
        var rep = await depot.DeclarerEpoqueAsync(A, decl, ct);
        Assert.False(rep.DejaDeclaree);
        Assert.True(rep.AvantPremierLot);
        Assert.True((await depot.DeclarerEpoqueAsync(A, decl, ct)).DejaDeclaree);
        var autre = await Assert.ThrowsAsync<PostgresException>(() => depot.DeclarerEpoqueAsync(A, decl with { DernierLotLocal = 5 }, ct));
        Assert.Equal("23505", autre.SqlState);

        // Effacement refusé avant le départ, même en simulation
        var avant = await Assert.ThrowsAsync<PostgresException>(() => DepartOfficine.EffacerAsync(db, A, false, null, ct));
        Assert.Equal("P0001", avant.SqlState);

        // Départ : certificat révoqué, officine résiliée
        var depart = await DepartOfficine.PartirAsync(db, A, "Administrateur essai", ct);
        Assert.Equal((1, false), (depart.CertificatsRevoques, depart.DejaPartie));
        var etat = await depot.CertificatAsync(new string('e', 64), ct);
        Assert.True(etat is { Revoque: true, Resiliee: true });

        // Export : un CSV par table miroir + ligne_remplacee, empreintes exactes, seulement les lignes de A
        var dossier = Directory.CreateTempSubdirectory("depart-officine-");
        try
        {
            var export = await DepartOfficine.ExporterAsync(db, A, Path.Combine(dossier.FullName, "officine-a.zip"), ct);
            await DepartOfficine.NoterExportAsync(db, A, "Administrateur essai", export, ct);
            Assert.False(File.Exists(export.Chemin + ".partiel"));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(export.Chemin))), export.EmpreinteArchive);
            using var zip = ZipFile.OpenRead(export.Chemin);
            var noms = zip.Entries.Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            Assert.Contains("produit.csv", noms);
            Assert.Contains("ligne_remplacee.csv", noms);
            Assert.Contains("cloud_epoque_changement.csv", noms);   // lot S6.5.3 : déclarations d'époque dans l'export
            Assert.Contains(DepartOfficine.FichierEmpreintes, noms);
            Assert.Equal(export.Fichiers.Count + 1, noms.Count);
            var lignesEmpreintes = Lire(zip, DepartOfficine.FichierEmpreintes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(export.Fichiers.Count, lignesEmpreintes.Length);
            foreach (var ligne in lignesEmpreintes)
            {
                var (empreinte, fichier) = (ligne[..64], ligne[66..]);
                Assert.Equal(empreinte, Convert.ToHexStringLower(SHA256.HashData(Octets(zip, fichier))));
            }
            Assert.Equal(2, export.Fichiers.Single(f => f.Fichier == "produit.csv").Lignes);
            Assert.Equal(1, export.Fichiers.Single(f => f.Fichier == "ligne_remplacee.csv").Lignes);
            var produit = Lire(zip, "produit.csv");
            Assert.False(Octets(zip, "produit.csv").AsSpan().StartsWith(Encoding.UTF8.Preamble), "UTF-8 sans BOM");
            Assert.StartsWith("officine_id;id;", produit);
            Assert.Contains("\"Périmé ; \"\"lot\"\"\nà retirer\"", produit);
            Assert.Contains(";nouvelle;", produit);
            Assert.DoesNotContain("900", produit);
            Assert.Contains("première version", Lire(zip, "ligne_remplacee.csv"));
            await Assert.ThrowsAsync<ArgumentException>(() => DepartOfficine.ExporterAsync(db, A, export.Chemin, ct));   // jamais écrasé
        }
        finally { dossier.Delete(true); }

        // Effacement refusé tant que la durée de conservation après départ n'est pas écoulée (simulation et confirmation)
        foreach (var confirmer in new[] { false, true })
        {
            var refus = await Assert.ThrowsAsync<PostgresException>(() => DepartOfficine.EffacerAsync(db, A, confirmer, "Administrateur essai", ct));
            Assert.Equal("P0001", refus.SqlState);
            Assert.Contains("non écoulée", refus.MessageText);
        }
        Assert.Equal(40, (await depot.MaximumsAsync(A, ct))["produit"]);   // rien n'a été effacé

        // Après le délai (départ reculé pour l'essai) : simulation sans effet, puis effacement confirmé
        await Executer(db, "UPDATE officine SET resiliee_le = now() - interval '7 years' WHERE id = $1", ct, A);
        var simulation = await DepartOfficine.EffacerAsync(db, A, false, null, ct);
        Assert.Equal(2, simulation.Single(l => l.Table == "produit").Lignes);
        Assert.Equal(40, (await depot.MaximumsAsync(A, ct))["produit"]);
        var effacement = await DepartOfficine.EffacerAsync(db, A, true, "Administrateur essai", ct);
        Assert.Equal(simulation, effacement);
        Assert.Empty(await depot.MaximumsAsync(A, ct));
        Assert.Equal(900, (await depot.MaximumsAsync(B, ct))["produit"]);
        await using var pv = db.CreateCommand("SELECT count(*) FROM journal_depart_officine WHERE officine_id = $1 AND action IN ('depart', 'export', 'effacement')");
        pv.Parameters.AddWithValue(A);
        Assert.Equal(3L, await pv.ExecuteScalarAsync(ct));
    }

    private static async Task Lot(NpgsqlDataSource db, Guid officine, int epoque, long numero, string contenu, CancellationToken ct)
    {
        await using var cnx = await db.OpenConnectionAsync(ct);
        await using var tx = await cnx.BeginTransactionAsync(ct);
        await using (var pose = new NpgsqlCommand("SELECT set_config('app.officine_id', $1, true)", cnx, tx))
        {
            pose.Parameters.AddWithValue(officine.ToString("D"));
            await pose.ExecuteScalarAsync(ct);
        }
        await using (var cmd = new NpgsqlCommand("SELECT statut FROM cloud_appliquer_lot($1, $2, $3, 'sync-v1', $4)", cnx, tx))
        {
            cmd.Parameters.AddWithValue(epoque);
            cmd.Parameters.AddWithValue(numero);
            cmd.Parameters.AddWithValue(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(contenu))));
            cmd.Parameters.AddWithValue(Encoding.UTF8.GetBytes(contenu));
            Assert.Equal("accepte", await cmd.ExecuteScalarAsync(ct));
        }
        await tx.CommitAsync(ct);
    }

    private static async Task Executer(NpgsqlDataSource db, string sql, CancellationToken ct, params object[] valeurs)
    {
        await using var cmd = db.CreateCommand(sql);
        foreach (var v in valeurs) cmd.Parameters.AddWithValue(v);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static byte[] Octets(ZipArchive zip, string fichier)
    {
        using var flux = zip.GetEntry(fichier)!.Open();
        using var copie = new MemoryStream();
        flux.CopyTo(copie);
        return copie.ToArray();
    }

    private static string Lire(ZipArchive zip, string fichier) => new UTF8Encoding(false).GetString(Octets(zip, fichier));
}
