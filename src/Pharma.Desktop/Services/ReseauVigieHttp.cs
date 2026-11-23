using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>
/// Accès réseau réel de la vigie : client HTTP à part (4 secondes par appel, certificat toujours vérifié par Windows), qui ne passe
/// pas par le relais d'adresse du poste : il vise toujours l'adresse précise qu'on lui donne.
/// </summary>
public sealed class ReseauVigieHttp(string nomPoste, HttpClient? client = null) : IReseauVigie, IDisposable
{
    private readonly HttpClient http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(4) };

    private static bool EstEchecReseau(Exception e) =>
        e is HttpRequestException or TaskCanceledException or IOException or System.Security.Authentication.AuthenticationException;

    private HttpRequestMessage Requete(HttpMethod methode, Uri uri)
    {
        var r = new HttpRequestMessage(methode, uri);
        r.Headers.Add(EnTetesInterServeurs.Poste, Uri.EscapeDataString(nomPoste));
        return r;
    }

    public async Task<EtatHealthcheck> SonderAsync(Uri adresse, CancellationToken ct)
    {
        try
        {
            using var requete = Requete(HttpMethod.Get, new Uri(adresse, "api/healthcheck"));
            using var rep = await http.SendAsync(requete, ct);
            if (!rep.IsSuccessStatusCode) return new(false, false, null);
            try
            {
                using var doc = JsonDocument.Parse(await rep.Content.ReadAsStringAsync(ct));
                var racine = doc.RootElement;
                var ecarte = racine.TryGetProperty("ecarte", out var e) && e.ValueKind == JsonValueKind.True;
                var role = racine.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
                return new(true, ecarte, role);
            }
            catch (JsonException) { return new(true, false, null); }
        }
        catch (Exception e) when (EstEchecReseau(e) && !ct.IsCancellationRequested) { return new(false, false, null); }
    }

    public async Task<ConsigneVigieDto?> RapporterAsync(Uri secours, RapportPosteDto rapport, string? jeton, CancellationToken ct)
    {
        try
        {
            using var requete = Requete(HttpMethod.Post, new Uri(secours, "api/vigie/rapport"));
            requete.Content = JsonContent.Create(rapport);
            if (jeton is { Length: > 0 }) requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
            using var rep = await http.SendAsync(requete, ct);
            return rep.IsSuccessStatusCode ? await rep.Content.ReadFromJsonAsync<ConsigneVigieDto>(ct) : null;
        }
        catch (Exception e) when ((EstEchecReseau(e) || e is JsonException) && !ct.IsCancellationRequested) { return null; }
    }

    public async Task<bool?> JetonAccepteAsync(Uri adresse, string jeton, CancellationToken ct)
    {
        try
        {
            using var requete = Requete(HttpMethod.Get, new Uri(adresse, "api/auth/permissions"));
            requete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
            using var rep = await http.SendAsync(requete, ct);
            if (rep.StatusCode == System.Net.HttpStatusCode.Unauthorized) return false;
            return rep.IsSuccessStatusCode ? true : null;
        }
        catch (Exception e) when (EstEchecReseau(e) && !ct.IsCancellationRequested) { return null; }
    }

    public void Dispose() => http.Dispose();
}
