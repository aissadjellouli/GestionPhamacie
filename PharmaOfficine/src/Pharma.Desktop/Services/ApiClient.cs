using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pharma.Shared;

namespace Pharma.Desktop.Services;

/// <summary>Session de l'employé connecté sur ce poste.</summary>
public sealed class SessionPoste
{
    public string? Jeton { get; private set; }
    public string? NomComplet { get; private set; }
    public DateTime ExpireLe { get; private set; }
    public bool EstConnecte => Jeton is not null && DateTime.UtcNow < ExpireLe;

    /// <summary>Prévient l'en-tête quand l'employé se connecte ou se déconnecte.</summary>
    public event Action? Changee;

    public void Ouvrir(ConnexionReponse r)
    {
        (Jeton, NomComplet, ExpireLe) = (r.Jeton, r.NomComplet, r.ExpireLe);
        Changee?.Invoke();
    }

    public void Fermer()
    {
        (Jeton, NomComplet) = (null, null);
        Changee?.Invoke();
    }
}

/// <summary>Lot FEFO trouvé, ou raison de l'absence (rupture, lots écartés).</summary>
public sealed record ReponseLotFefo(LotFefoDto? Lot, IReadOnlyList<LotEcarteDto> Ecartes, string? Erreur);

/// <summary>Appels à l'API locale. Chaque méthode renvoie le résultat ou un message d'erreur lisible.</summary>
public sealed class ApiClient(HttpClient http, SessionPoste session)
{
    private const string Injoignable = "Serveur de la pharmacie injoignable : vérifiez que le PC serveur est allumé.";

    public async Task<string?> ConnecterAsync(string identifiant, string motDePasse)
    {
        try
        {
            var rep = await http.PostAsJsonAsync("api/auth/connexion", new ConnexionRequete(identifiant, motDePasse));
            if (!rep.IsSuccessStatusCode) return await MessageAsync(rep);
            session.Ouvrir((await rep.Content.ReadFromJsonAsync<ConnexionReponse>())!);
            return null;
        }
        catch (HttpRequestException)
        {
            return Injoignable;
        }
    }

    public Task<(List<AlerteDto>, string?)> AlertesAsync() => GetAsync<List<AlerteDto>>("api/alertes", []);

    public Task<(List<ProduitDto>, string?)> RechercherProduitsAsync(string q) =>
        GetAsync<List<ProduitDto>>($"api/produits?q={Uri.EscapeDataString(q)}", []);

    public Task<(List<ProduitAlternatifDto>, string?)> AlternativesAsync(long produitId) =>
        GetAsync<List<ProduitAlternatifDto>>($"api/produits/{produitId}/alternatives", []);

    public Task<(EtatStockDto?, string?)> StockAsync() => GetAsync<EtatStockDto?>("api/stock", null);

    public Task<(EtatEtiquetageDto?, string?)> EtiquetageAsync() =>
        GetAsync<EtatEtiquetageDto?>("api/tracabilite/etiquetage", null);

    public Task<(ParcoursBoiteDto?, string?)> ParcoursBoiteAsync(string code) =>
        GetAsync<ParcoursBoiteDto?>($"api/tracabilite/boites/{Uri.EscapeDataString(code)}", null);

    /// <summary>Prévient l'en-tête (badge) quand une alerte est reportée ou traitée.</summary>
    public event Action? AlertesModifiees;

    public async Task<string?> ReporterAlerteAsync(long id, DateOnly jusquAu)
    {
        var (_, erreur) = await PostAsync<ReporterAlerteRequete, object>($"api/alertes/{id}/reporter", new(jusquAu));
        if (erreur is null) AlertesModifiees?.Invoke();
        return erreur;
    }

    public async Task<string?> TraiterAlerteAsync(long id, string? actionPrise)
    {
        var (_, erreur) = await PostAsync<TraiterAlerteRequete, object>($"api/alertes/{id}/traiter", new(actionPrise));
        if (erreur is null) AlertesModifiees?.Invoke();
        return erreur;
    }

    public Task<(RappelLotDto?, string?)> RappelerLotAsync(long produitId, string numeroLot, string motif) =>
        PostAsync<RappelLotRequete, RappelLotDto>("api/tracabilite/rappels", new(produitId, numeroLot, motif));

    /// <summary>Le 404 n'est pas une erreur ici : il signifie « aucun lot vendable » et liste les lots écartés.</summary>
    public async Task<ReponseLotFefo> LotFefoAsync(long produitId)
    {
        try
        {
            using var rep = await EnvoyerAsync($"api/produits/{produitId}/lot-fefo");
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return new(null, [], SessionExpiree());
            if (rep.StatusCode == HttpStatusCode.NotFound)
            {
                var aucun = await rep.Content.ReadFromJsonAsync<AucunLotDto>();
                return new(null, aucun?.Ecartes ?? [], null);
            }
            if (!rep.IsSuccessStatusCode) return new(null, [], await MessageAsync(rep));
            var lot = (await rep.Content.ReadFromJsonAsync<LotFefoDto>())!;
            return new(lot, lot.Ecartes, null);
        }
        catch (HttpRequestException)
        {
            return new(null, [], Injoignable);
        }
    }

    private async Task<(T, string?)> GetAsync<T>(string url, T defaut)
    {
        try
        {
            using var rep = await EnvoyerAsync(url);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (defaut, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (defaut, await MessageAsync(rep));
            return ((await rep.Content.ReadFromJsonAsync<T>())!, null);
        }
        catch (HttpRequestException)
        {
            return (defaut, Injoignable);
        }
    }

    /// <summary>POST JSON. Une réponse 204 (sans contenu) renvoie default.</summary>
    private async Task<(TRep?, string?)> PostAsync<TReq, TRep>(string url, TReq corps)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(corps) };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
            using var rep = await http.SendAsync(req);
            if (rep.StatusCode == HttpStatusCode.Unauthorized) return (default, SessionExpiree());
            if (!rep.IsSuccessStatusCode) return (default, await MessageAsync(rep));
            if (rep.StatusCode == HttpStatusCode.NoContent) return (default, null);
            return (await rep.Content.ReadFromJsonAsync<TRep>(), null);
        }
        catch (HttpRequestException)
        {
            return (default, Injoignable);
        }
    }

    private Task<HttpResponseMessage> EnvoyerAsync(string url)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.Jeton);
        return http.SendAsync(req);
    }

    private string SessionExpiree()
    {
        session.Fermer();
        return "Session expirée : reconnectez-vous.";
    }

    private static async Task<string> MessageAsync(HttpResponseMessage rep)
    {
        try
        {
            // ErreurDto { message } ou ProblemDetails { detail }
            using var doc = System.Text.Json.JsonDocument.Parse(await rep.Content.ReadAsStringAsync());
            foreach (var nom in new[] { "message", "detail", "title" })
                if (doc.RootElement.TryGetProperty(nom, out var v) && v.GetString() is { Length: > 0 } texte)
                    return texte;
        }
        catch { /* corps vide ou non JSON */ }
        return $"Erreur {(int)rep.StatusCode}.";
    }
}
