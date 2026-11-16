using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Pharma.Cloud.Api.Securite;
using Pharma.Shared;

// Test de charge du serveur central (lot S2), lancé à la main sur une base d'essai :
//   Pharma.Cloud.Charge --officines 1000 --lots 5 --lignes 200 --paralleles 64 [--adresse https://127.0.0.1:5090]
// Variables : PHARMA_CLOUD_DOSSIER, PHARMA_CLOUD_MOT_DE_PASSE (autorité, pour signer les certificats des officines simulées),
// PHARMA_CLOUD_ADMIN_CONNEXION (propriétaire : officines et certificats d'essai, mesure de la place occupée).
// Chaque officine simulée : clé et certificat propres (signés directement par l'autorité, sans code d'activation), jeton, puis
// dépôt de ses lots sync-v1 (lignes « vente ») dans l'ordre. Mesure : lots/s, lignes/s, place occupée par officine.
// Les officines d'essai portent le libellé « charge-essai » : à lancer sur une base d'essai jetable (lots reçus en ajout seul).

var opt = args.Select((a, i) => (a, i)).Where(x => x.a.StartsWith("--")).ToDictionary(x => x.a, x => x.i + 1 < args.Length && !args[x.i + 1].StartsWith("--") ? args[x.i + 1] : "");
int Ent(string n, int d) => opt.TryGetValue(n, out var v) && int.TryParse(v, out var x) ? x : d;
var adresse = opt.GetValueOrDefault("--adresse") is { Length: > 0 } a ? a : "https://127.0.0.1:5090";
var dossier = Environment.GetEnvironmentVariable("PHARMA_CLOUD_DOSSIER") ?? throw new InvalidOperationException("PHARMA_CLOUD_DOSSIER absente.");
var mdp = Environment.GetEnvironmentVariable("PHARMA_CLOUD_MOT_DE_PASSE") ?? throw new InvalidOperationException("PHARMA_CLOUD_MOT_DE_PASSE absente.");
await using var admin = NpgsqlDataSource.Create(Environment.GetEnvironmentVariable("PHARMA_CLOUD_ADMIN_CONNEXION") ?? throw new InvalidOperationException("PHARMA_CLOUD_ADMIN_CONNEXION absente."));

int n = Ent("--officines", 100), lotsParOfficine = Ent("--lots", 5), lignesParLot = Ent("--lignes", 200), paralleles = Ent("--paralleles", 32);
var ac = AutoriteCertification.Charger(dossier, mdp);
Console.WriteLine($"Préparation de {n} officines simulées (certificats signés par l'autorité)…");

async Task<long> PlaceAsync()
{
    await using var c = admin.CreateCommand("SELECT coalesce(sum(pg_total_relation_size(c.oid)), 0)::bigint FROM pg_class c JOIN pg_namespace s ON s.oid = c.relnamespace WHERE s.nspname = 'public' AND c.relkind IN ('r', 'p')");
    return (long)(await c.ExecuteScalarAsync())!;
}
var placeAvant = await PlaceAsync();

var officines = new ConcurrentBag<(Guid Id, X509Certificate2 Cert)>();
var prep = Stopwatch.StartNew();
await Parallel.ForEachAsync(Enumerable.Range(0, n), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (_, ct) =>
{
    var id = Guid.NewGuid();
    using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var csr = new CertificateRequest("CN=charge", cle, HashAlgorithmName.SHA256).CreateSigningRequestPem();
    using var cert = ac.SignerDemande(csr, id, "principal", DateTimeOffset.UtcNow);
    await using var cnx = await admin.OpenConnectionAsync(ct);
    await using var c = new NpgsqlCommand("""
        WITH o AS (INSERT INTO officine (id, libelle) VALUES ($1, 'charge-essai') RETURNING id)
        INSERT INTO certificat (empreinte, officine_id, serveur, numero_serie, echeance) SELECT $2, o.id, 'principal', $3, $4 FROM o;
        """, cnx);
    c.Parameters.AddWithValue(id);
    c.Parameters.AddWithValue(AutoriteCertification.Empreinte(cert));
    c.Parameters.AddWithValue(cert.SerialNumber);
    c.Parameters.AddWithValue(cert.NotAfter.ToUniversalTime());
    await c.ExecuteNonQueryAsync(ct);
    using var complet = cert.CopyWithPrivateKey(cle);
    officines.Add((id, X509CertificateLoader.LoadPkcs12(complet.Export(X509ContentType.Pkcs12), null)));
});
Console.WriteLine($"Préparation : {prep.Elapsed.TotalSeconds:0.0} s");

var donnees = """{"id":{0},"numero":"T-{0}","date_vente":"2026-11-16T10:00:00+01:00","utilisateur_id":3,"patient_id":"P-0123456789abcdef01234567","type":"comptant","montant_total":1250.00,"part_organisme":0.00,"part_patient":1250.00,"statut":"validee","remise":0.00,"session_caisse_id":12}""";
long lots = 0, lignes = 0, erreurs = 0;
var latences = new ConcurrentBag<double>();
var chrono = Stopwatch.StartNew();
await Parallel.ForEachAsync(officines, new ParallelOptions { MaxDegreeOfParallelism = paralleles }, async (o, ct) =>
{
    using var http = new HttpClient(new SocketsHttpHandler
    {
        SslOptions =
        {
            ClientCertificates = [o.Cert],
            RemoteCertificateValidationCallback = (_, c, ch, e) => EpinglageServeur.Accepter(c as X509Certificate2, ch, e, ac.Racine, null)
        }
    }) { BaseAddress = new Uri(adresse + "/") };
    try
    {
        using var rj = await http.PostAsync("api/v1/jeton", null, ct);
        rj.EnsureSuccessStatusCode();
        var jeton = (await rj.Content.ReadFromJsonAsync<ReponseJetonDto>(ct))!.Jeton;
        for (var l = 1; l <= lotsParOfficine; l++)
        {
            var lignesLot = Enumerable.Range((l - 1) * lignesParLot + 1, lignesParLot).Select(i => new LigneSyncDto(i, "vente", i.ToString(), ContratSync.Creation,
                DateTime.UtcNow, donnees.Replace("{0}", i.ToString()))).ToList();
            var lot = SerialisationSyncV1.AvecEmpreinte(new LotSyncDto(new EnTeteLotSyncDto(ContratSync.VersionV1, o.Id, 1, l, lignesLot.Count, ""), lignesLot));
            using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/lots") { Content = new ByteArrayContent(SerialisationSyncV1.Serialiser(lot)) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
            var t = Stopwatch.GetTimestamp();
            using var rep = await http.SendAsync(req, ct);
            latences.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            if (!rep.IsSuccessStatusCode) { Interlocked.Increment(ref erreurs); Console.Error.WriteLine($"{(int)rep.StatusCode} {await rep.Content.ReadAsStringAsync(ct)}"); break; }
            Interlocked.Increment(ref lots);
            Interlocked.Add(ref lignes, lignesLot.Count);
        }
    }
    catch (HttpRequestException e) { Interlocked.Increment(ref erreurs); Console.Error.WriteLine(e.Message); }
});
chrono.Stop();
var placeApres = await PlaceAsync();
var tri = latences.Order().ToArray();
double Centile(double p) => tri.Length == 0 ? 0 : tri[Math.Min(tri.Length - 1, (int)(p * tri.Length))];
Console.WriteLine($"Officines : {n} ; lots : {lots} ; lignes : {lignes} ; erreurs : {erreurs} ; durée : {chrono.Elapsed.TotalSeconds:0.0} s");
Console.WriteLine($"Débit : {lots / chrono.Elapsed.TotalSeconds:0.0} lots/s ; {lignes / chrono.Elapsed.TotalSeconds:0} lignes/s");
Console.WriteLine($"Latence d'un dépôt : médiane {Centile(0.5):0} ms ; 95 % {Centile(0.95):0} ms ; max {(tri.Length == 0 ? 0 : tri[^1]):0} ms");
Console.WriteLine($"Place occupée : +{(placeApres - placeAvant) / 1024.0 / 1024.0:0.0} Mo au total ; {(placeApres - placeAvant) / (double)n / 1024.0:0.0} Ko par officine ({lotsParOfficine * lignesParLot} lignes)");
return erreurs == 0 ? 0 : 1;
