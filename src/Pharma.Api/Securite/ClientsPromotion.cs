using System.Text.Json;
using Pharma.Application.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Securite;

// ---- Vague 2, lot V2-4 : appels signés du 2e serveur vers le serveur 1 (retrait, alertes) -----------------------------------

/// <summary>POST /api/inter-serveurs/retrait. Délai de 20 s : le serveur 1 attend jusqu'à 10 s la fin des transactions de son API. Réponse non fiable ou autre que 200 : pas de LSN.</summary>
public sealed class ClientRetraitServeur1Signe(IClientInterServeurs client) : IClientRetraitServeur1
{
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(20);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReponseRetrait> DemanderRetraitAsync(CancellationToken ct = default)
    {
        var r = await client.EnvoyerAsync(HttpMethod.Post, InterServeursMiddleware.Prefixe + "/retrait", "{}"u8.ToArray(), Delai, ct);
        if (!r.Fiable) return new ReponseRetrait(false, r.Statut, null);
        if (r.Statut != 200) return new ReponseRetrait(true, r.Statut, null);
        try
        {
            var dto = JsonSerializer.Deserialize<RetraitInterServeursDto>(r.Corps, Json);
            return new ReponseRetrait(true, 200, dto is { Ecarte: true, LsnFinal.Length: > 0 } ? dto.LsnFinal : null);
        }
        catch (JsonException) { return new ReponseRetrait(false, 200, null); }
    }
}

/// <summary>POST /api/inter-serveurs/alerte (202 attendu). Aucune exception : serveur 1 injoignable, refus ou réponse non fiable = faux.</summary>
public sealed class AlertesVersServeur1Signe(IClientInterServeurs client) : IAlertesVersServeur1
{
    public static readonly TimeSpan Delai = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> EnvoyerAsync(string type, string? poste, string message, DateTime survenueUtc, CancellationToken ct = default)
    {
        var corps = JsonSerializer.SerializeToUtf8Bytes(new AlerteInterServeursDto(type, poste, message, DateTime.SpecifyKind(survenueUtc, DateTimeKind.Utc)), Json);
        var r = await client.EnvoyerAsync(HttpMethod.Post, InterServeursMiddleware.Prefixe + "/alerte", corps, Delai, ct);
        return r is { Fiable: true, Statut: 202 };
    }
}
