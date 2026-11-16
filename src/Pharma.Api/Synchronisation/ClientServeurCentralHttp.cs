using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Pharma.Application.Synchronisation;
using Pharma.Shared;

namespace Pharma.Api.Synchronisation;

/// <summary>
/// Client du serveur central (lot S2) : HTTPS (TLS 1.2 minimum) avec le certificat client de ce serveur, autorité du serveur central
/// épinglée, jeton court (10 min) redemandé avant expiration. Initié par l'officine seulement (aucun port entrant). Aucune donnée
/// personnelle dans les messages d'erreur rendus (codes et motifs du serveur central).
/// </summary>
public sealed class ClientServeurCentralHttp(ICertificatSync certificats, ILogger<ClientServeurCentralHttp> logger) : IClientServeurCentral, IClientChargementCentral
{
    private static readonly TimeSpan Delai = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim verrou = new(1, 1);
    private string? jeton;
    private DateTimeOffset jetonExpire;
    private string? jetonCertificat;
    private (string Cle, HttpClient Http, X509Certificate2 Cert, X509Certificate2? Autorite)? cache;

    public async Task<ReponseActivationSync> ActiverAsync(string adresse, string code, string demandePem, Guid officine, CancellationToken ct = default)
    {
        if (CodeActivationSync.Lire(code) is not { } lu) throw new ServeurCentralException("code d'activation mal formé (vérifiez la saisie, point compris)");
        using var http = Client(adresse, null, null, lu.Epinglage);
        using var rep = await http.PostAsJsonAsync("api/v1/activation", new DemandeActivationDto(code, demandePem, officine), ct);
        var r = await LireAsync<ReponseActivationDto>(rep, ct);
        return new ReponseActivationSync(r.OfficineId, r.Serveur, r.CertificatPem, r.AutoritePem, r.Echeance.UtcDateTime, lu.Epinglage);
    }

    public async Task<AccuseLotSync> DeposerAsync(string adresse, byte[] lot, long versionDescendante, CancellationToken ct = default)
    {
        var (http, cert) = await ClientCertifieAsync(adresse, ct);
        var j = await JetonAsync(http, cert, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/lots") { Content = new ByteArrayContent(lot) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", j);
        req.Headers.Add(ChangementsDescendantsDto.EnTeteVersion, versionDescendante.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var rep = await http.SendAsync(req, ct);
        if (rep.StatusCode == System.Net.HttpStatusCode.Conflict && rep.Content.Headers.ContentType?.MediaType == "application/json")
        {
            // Trou : accusé portant le dernier lot reçu
            var a = await rep.Content.ReadFromJsonAsync<AccuseLotDto>(ct);
            if (a is not null) return new AccuseLotSync(a.Epoque, a.Numero, a.Statut, a.DernierRecu, Descendants(a.Descendants));
        }
        if (rep.StatusCode == System.Net.HttpStatusCode.Unauthorized) await OublierJetonAsync(ct);
        var acc = await LireAsync<AccuseLotDto>(rep, ct);
        return new AccuseLotSync(acc.Epoque, acc.Numero, acc.Statut, acc.DernierRecu, Descendants(acc.Descendants));
    }

    public async Task<ReponseActivationSync> RenouvelerAsync(string adresse, string demandePem, CancellationToken ct = default)
    {
        var (http, cert) = await ClientCertifieAsync(adresse, ct);
        var j = await JetonAsync(http, cert, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, "api/v1/certificat/renouveler") { Content = JsonContent.Create(new DemandeRenouvellementDto(demandePem)) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", j);
        using var rep = await http.SendAsync(req, ct);
        var r = await LireAsync<ReponseActivationDto>(rep, ct);
        await OublierJetonAsync(ct); // nouveau certificat : nouveau jeton (et nouveau client au prochain appel)
        logger.LogInformation("Certificat de synchronisation renouvelé jusqu'au {Echeance:yyyy-MM-dd}", r.Echeance);
        return new ReponseActivationSync(r.OfficineId, r.Serveur, r.CertificatPem, r.AutoritePem, r.Echeance.UtcDateTime, null);
    }

    /// <summary>Échange léger de la voie descendante (chaque passage, même sans lot à envoyer).</summary>
    public async Task<DescendantsRecus> EchangerAsync(string adresse, long versionAppliquee, CancellationToken ct = default) =>
        Descendants(await PosterAsync<EchangeDescendantDto, ChangementsDescendantsDto>(adresse, "api/v1/echange", new EchangeDescendantDto(versionAppliquee), ct));

    /// <summary>Plafonds de la voie descendante : réponse (MaxReponse, toutes routes), nombre d'éléments, taille d'un élément.</summary>
    public const int MaxReponse = 1024 * 1024, MaxElements = 1000, MaxDonnees = 64 * 1024;

    /// <summary>
    /// Contrat desc-v1 seulement ; au-delà de MaxElements, seuls les premiers par version sont gardés ; un élément trop gros, sans
    /// table ni clé ou dont les données ne sont pas un objet JSON est ignoré (journalisé sans données) ; il reviendra au passage suivant.
    /// </summary>
    public DescendantsRecus Descendants(ChangementsDescendantsDto? d)
    {
        if (d is null || d.Changements is null || d.Changements.Count == 0) return DescendantsRecus.Aucun;
        if (d.Contrat != ChangementsDescendantsDto.ContratV1)
        {
            logger.LogWarning("Synchronisation : contrat descendant {Contrat} non pris en charge, éléments ignorés", d.Contrat);
            return DescendantsRecus.Aucun;
        }
        var gardes = new List<ChangementDescendantSync>();
        foreach (var c in d.Changements.Where(c => c is not null).OrderBy(c => c.Version).Take(MaxElements))
        {
            if (string.IsNullOrEmpty(c.Table) || string.IsNullOrEmpty(c.Cle) || c.DonneesJson is null || c.DonneesJson.Length > MaxDonnees || !ObjetJson(c.DonneesJson))
            {
                logger.LogWarning("Synchronisation : élément descendant {Table} version {Version} ignoré (mal formé ou trop gros)", c.Table?.Length > 63 ? "?" : c.Table, c.Version);
                continue;
            }
            gardes.Add(new ChangementDescendantSync(c.Table, c.Cle, c.Version, c.DonneesJson));
        }
        if (d.Changements.Count > MaxElements)
            logger.LogWarning("Synchronisation : {Nombre} éléments descendants reçus, {Max} gardés pour ce passage", d.Changements.Count, MaxElements);
        return new DescendantsRecus(gardes, d.VersionMax);
    }

    private static bool ObjetJson(string json)
    {
        try { using var doc = System.Text.Json.JsonDocument.Parse(json); return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object; }
        catch (System.Text.Json.JsonException) { return false; }
    }
    // ---- Lot S6 : chargement complet ----
    public async Task<ReponseDebutChargementSync> DebuterAsync(string adresse, DebutChargementSync d, CancellationToken ct = default)
    {
        var r = await PosterAsync<DebutChargementDto, ReponseDebutChargementDto>(adresse, "api/v1/chargement/debut",
            new DebutChargementDto(d.Numero, d.Epoque, d.Repere, d.ReprendreApres, d.Motif, d.Tables), ct);
        if (r.LotsAbandonnes > 0)
            logger.LogWarning("Synchronisation : {Lots} lot(s) perdus par le serveur central remplacés par le chargement complet {Numero}", r.LotsAbandonnes, r.Numero);
        return new ReponseDebutChargementSync(r.Numero, r.DernierRecu, r.LotsAbandonnes);
    }

    public async Task<ReponseFinChargementSync> TerminerAsync(string adresse, int numero, IReadOnlyDictionary<string, long> lignes, CancellationToken ct = default)
    {
        var r = await PosterAsync<FinChargementDto, ReponseFinChargementDto>(adresse, "api/v1/chargement/fin", new FinChargementDto(numero, lignes), ct);
        return new ReponseFinChargementSync(r.Numero, r.Absentes, r.AbsentesParTable);
    }

    private async Task<TOut> PosterAsync<TIn, TOut>(string adresse, string route, TIn corps, CancellationToken ct)
    {
        var (http, cert) = await ClientCertifieAsync(adresse, ct);
        var j = await JetonAsync(http, cert, ct);
        using var req = new HttpRequestMessage(HttpMethod.Post, route) { Content = JsonContent.Create(corps) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", j);
        using var rep = await http.SendAsync(req, ct);
        if (rep.StatusCode == System.Net.HttpStatusCode.Unauthorized) await OublierJetonAsync(ct);
        return await LireAsync<TOut>(rep, ct);
    }

    /// <summary>Client HTTP réutilisé tant que l'adresse, le certificat et l'autorité ne changent pas (connexions TLS gardées).</summary>
    private async Task<(HttpClient, X509Certificate2)> ClientCertifieAsync(string adresse, CancellationToken ct)
    {
        await verrou.WaitAsync(ct);
        try
        {
            var info = certificats.Lire() ?? throw new ServeurCentralException("certificat de synchronisation absent");
            var cle = adresse + "|" + info.Empreinte + "|" + info.EmpreinteAutorite;
            if (cache is { } c && c.Cle == cle) return (c.Http, c.Cert);
            if (cache is { } ancien) { ancien.Http.Dispose(); ancien.Cert.Dispose(); ancien.Autorite?.Dispose(); }
            cache = null;
            jeton = null;
            var cert = certificats.CertificatClient() ?? throw new ServeurCentralException("certificat de synchronisation absent");
            var autorite = certificats.Autorite();
            var http = Client(adresse, cert, autorite, null);
            cache = (cle, http, cert, autorite);
            return (http, cert);
        }
        finally { verrou.Release(); }
    }

    private async Task OublierJetonAsync(CancellationToken ct)
    {
        await verrou.WaitAsync(ct);
        try { jeton = null; }
        finally { verrou.Release(); }
    }

    private async Task<string> JetonAsync(HttpClient http, X509Certificate2 cert, CancellationToken ct)
    {
        await verrou.WaitAsync(ct);
        try
        {
            if (jeton is not null && jetonCertificat == cert.Thumbprint && jetonExpire > DateTimeOffset.UtcNow.AddMinutes(1)) return jeton;
            using var rep = await http.PostAsync("api/v1/jeton", null, ct);
            var r = await LireAsync<ReponseJetonDto>(rep, ct);
            (jeton, jetonExpire, jetonCertificat) = (r.Jeton, r.Expire, cert.Thumbprint);
            return r.Jeton;
        }
        finally { verrou.Release(); }
    }

    private static HttpClient Client(string adresse, X509Certificate2? cert, X509Certificate2? autorite, string? epinglage)
    {
        var gestionnaire = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            SslOptions =
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = cert is null ? null : new X509CertificateCollection { cert },
                RemoteCertificateValidationCallback = (_, c, chaine, erreurs) =>
                    EpinglageServeur.Accepter(c as X509Certificate2 ?? (c is null ? null : X509CertificateLoader.LoadCertificate(c.GetRawCertData())), chaine, erreurs, autorite, epinglage)
            }
        };
        return new HttpClient(gestionnaire, disposeHandler: true) { BaseAddress = new Uri(adresse.TrimEnd('/') + "/"), Timeout = Delai, MaxResponseContentBufferSize = MaxReponse };
    }

    private static async Task<T> LireAsync<T>(HttpResponseMessage rep, CancellationToken ct)
    {
        if (!rep.IsSuccessStatusCode)
        {
            string? detail = null;
            try { detail = (await rep.Content.ReadFromJsonAsync<Microsoft.AspNetCore.Mvc.ProblemDetails>(ct))?.Detail; }
            catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or InvalidOperationException) { }
            // 429 avec un motif = quota ou plafond du serveur central (limite par jour, par chargement, chargements sur 24 h) ;
            // la limite de débit seule répond 429 sans motif
            throw new ServeurCentralException($"serveur central : {(int)rep.StatusCode} {detail ?? rep.ReasonPhrase}".Trim(), (int)rep.StatusCode,
                quota: (int)rep.StatusCode == 429 && !string.IsNullOrWhiteSpace(detail));
        }
        return await rep.Content.ReadFromJsonAsync<T>(ct) ?? throw new ServeurCentralException("réponse vide du serveur central");
    }
}
