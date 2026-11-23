using Microsoft.AspNetCore.Http;
using Pharma.Cloud.Api;

namespace Pharma.Cloud.Tests;

/// <summary>
/// Dépôt d'un lot au central (revue du 2026-11-22) : corps lu une seule fois, certificat client vérifié une seule fois par appel,
/// aucune lecture SQL sur une ligne absente.
/// </summary>
public class DepotLotsCentralTests
{
    private static HttpRequest Requete(byte[] corps, long? longueur)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(corps);
        ctx.Request.ContentLength = longueur;
        return ctx.Request;
    }

    [Fact]
    public async Task Corps_lu_a_la_taille_annoncee_ou_sans_annonce()
    {
        var corps = System.Text.Encoding.UTF8.GetBytes("{\"lot\":\"contenu d'essai\"}");
        Assert.Equal(corps, await PointsEntree.LireCorpsAsync(Requete(corps, corps.Length), CancellationToken.None));
        Assert.Equal(corps, await PointsEntree.LireCorpsAsync(Requete(corps, null), CancellationToken.None));
        Assert.Empty(await PointsEntree.LireCorpsAsync(Requete([], 0), CancellationToken.None));
    }

    [Fact]
    public async Task Corps_annonce_plus_grand_que_le_maximum_refuse_avant_toute_allocation()
    {
        // Revue du 2026-11-23 : new byte[Content-Length] était fait avant tout contrôle. Le corps réel est vide : seul l'en-tête compte
        var requete = Requete([], Pharma.Cloud.Api.Reception.ControleLot.TailleMax + 1L);
        await Assert.ThrowsAsync<PointsEntree.CorpsTropGrosException>(() => PointsEntree.LireCorpsAsync(requete, CancellationToken.None));
        await Assert.ThrowsAsync<PointsEntree.CorpsTropGrosException>(() => PointsEntree.LireCorpsAsync(Requete([], int.MaxValue), CancellationToken.None));
        // Au maximum annoncé : non refusé pour sa taille (le corps trop court l'est pour une autre raison)
        await Assert.ThrowsAsync<EndOfStreamException>(() => PointsEntree.LireCorpsAsync(Requete([1], 16), CancellationToken.None, maximum: 16));
        await Assert.ThrowsAsync<PointsEntree.CorpsTropGrosException>(() => PointsEntree.LireCorpsAsync(Requete([1], 17), CancellationToken.None, maximum: 16));
    }

    [Fact]
    public void Lot_illisible_le_message_de_l_exception_json_n_est_jamais_ecrit()
    {
        // Revue du 2026-11-23 : e.Message d'une JsonException peut citer des données du lot (journal de sécurité, journal applicatif)
        var c = Pharma.Cloud.Api.Reception.ControleLot.Controler("{\"secret-donnee-patient\": ,}"u8, Guid.NewGuid());
        Assert.False(c.Valide);
        Assert.Equal(400, c.CodeHttp);
        Assert.DoesNotContain("secret-donnee-patient", c.Refus);
        Assert.StartsWith("Lot illisible ou non conforme au contrat", c.Refus);
    }

    [Fact]
    public async Task Corps_plus_court_que_l_annonce_refuse()
    {
        await Assert.ThrowsAsync<EndOfStreamException>(() => PointsEntree.LireCorpsAsync(Requete([1, 2, 3], 10), CancellationToken.None));
    }

    private static string Source(params string[] parties)
    {
        var dossier = new DirectoryInfo(AppContext.BaseDirectory);
        while (dossier is not null && !File.Exists(Path.Combine(dossier.FullName, "PharmaOfficine.slnx"))) dossier = dossier.Parent;
        return File.ReadAllText(Path.Combine([dossier!.FullName, "src", "Pharma.Cloud.Api", .. parties]));
    }

    [Fact]
    public void Aucune_lecture_sql_sur_une_ligne_absente_et_certificat_verifie_une_fois()
    {
        var depot = Source("Reception", "DepotCentral.cs");
        Assert.DoesNotContain("await r.ReadAsync(ct);", depot);          // une ligne attendue : LigneAttendueAsync
        Assert.Equal(5, System.Text.RegularExpressions.Regex.Matches(depot, @"LigneAttendueAsync\(r, ""cloud_\w+"", ct\)").Count);
        var points = Source("PointsEntree.cs");
        Assert.Contains("ctx.Items[Program.CertificatVerifie] is X509Certificate2 v && ReferenceEquals(v, c)", points);
        Assert.Contains("catch (ReponseCentraleVideException v)", points);
        // Revue du 2026-11-23 : plus aucune lecture d'une ligne unique sans contrôle dans les accès du poste, ni dans les départs
        foreach (var fichier in new[] { new[] { "Comptes", "RattachementPoste.cs" }, ["Comptes", "AccesDistantsPoste.cs"], ["Depart", "DepartOfficine.cs"], ["Comptes", "ComptesDistantsAdministration.cs"] })
        {
            var code = Source(fichier);
            Assert.DoesNotContain("await r.ReadAsync(ct);", code);
            Assert.Contains("LigneAttendueAsync(r, ", code);
        }
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(points, @"catch \(ReponseCentraleVideException v\) \{ return AnomalieBase").Count);   // rattachement, liste, droits, retrait
        Assert.DoesNotContain("flux.ToArray();\n            var c = ControleLot", points.Replace("\r\n", "\n"));
        Assert.Contains("GetRequiredService<TimeProvider>().GetUtcNow()", Source("Program.cs"));
    }
}
