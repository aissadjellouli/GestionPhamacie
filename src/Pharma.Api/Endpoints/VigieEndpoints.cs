using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using Pharma.Api.Securite;
using Pharma.Application.Commun;
using Pharma.Application.SecondServeur;
using Pharma.Infrastructure.SecondServeur;
using Pharma.Shared;

namespace Pharma.Api.Endpoints;

/// <summary>
/// Lecteur de l'état du serveur 1 pour la vigie : appel signé de GET /api/inter-serveurs/etat (V2-2) converti en <see cref="EtatServeur1"/>.
/// Null si la réponse n'est pas fiable (réseau, délai, non signée, 401/403, code autre que 200) : jamais de décision sur une erreur d'outillage.
/// </summary>
public sealed class LecteurEtatServeur1Signe(Pharma.Api.Securite.IClientInterServeurs client) : ILecteurEtatServeur1
{
    public async Task<EtatServeur1?> EtatServeur1Async(CancellationToken ct = default)
    {
        var r = await client.LireEtatAutreAsync(ct);
        return r is { Issue: Pharma.Api.Securite.IssueInterServeurs.Ok, Statut: 200, Etat: { } e }
            ? new EtatServeur1(e.Serveur, e.Role, e.Timeline, e.EnRecovery, e.VersionApi, e.EmpreinteJeton)
            : null;
    }
}

/// <summary>État de l'autre serveur pour la levée automatique de l'isolement : appel signé (V2-2). Joignable = réponse valable (200 signée) seulement.</summary>
public sealed class LecteurEtatAutreServeurSigne(Pharma.Api.Securite.IClientInterServeurs client) : ILecteurEtatAutreServeur
{
    public async Task<EtatAutreServeurLu> LireAsync(CancellationToken ct = default)
    {
        var r = await client.LireEtatAutreAsync(ct);
        return r is { Issue: Pharma.Api.Securite.IssueInterServeurs.Ok, Statut: 200, Etat: { } e } ? new EtatAutreServeurLu(true, e.Role, e.EnRecovery) : new EtatAutreServeurLu(false);
    }
}

public static class VigieServices
{
    /// <summary>Enregistre la vigie du 2e serveur (V2-3). Réglages : section SecondServeur:Bascule, bornés. Journal : dossier des journaux du serveur.</summary>
    public static IServiceCollection AjouterVigieSecondServeur(this IServiceCollection services, IConfiguration config, string dossierJournaux)
    {
        var reglages = config.GetSection(ReglagesBascule.Section).Get<ReglagesBascule>() ?? new ReglagesBascule();
        services.AddSingleton(reglages.Borner());
        services.AddSingleton<RegistreRapportsPostes>();
        // Présence des postes (script 165) : lecture des postes connus (V2-3) et écriture de la présence (V2-1b), même table
        services.AddSingleton<PresencePostesSql>(sp => new PresencePostesSql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IPostesConnusDepot>(sp => sp.GetRequiredService<PresencePostesSql>());
        services.AddSingleton<IPresenceDepot>(sp => sp.GetRequiredService<PresencePostesSql>());
        services.AddSingleton<RegistrePostesVus>();
        services.AddSingleton<PresencePostes>();
        // Serveur 1 isolé (Q32) : copie connectée ? état signé du 2e serveur, décision toutes les 5 s (tâche SurveillanceIsolementTache)
        services.AddSingleton<ICopieConnectee>(sp => new CopieConnecteeSql(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<ILecteurEtatAutreServeur, LecteurEtatAutreServeurSigne>();
        services.AddSingleton<SurveillerIsolement>();
        services.AddSingleton<IJournalDecisions>(sp => new JournalDecisionsFichier(dossierJournaux, () => sp.GetRequiredService<IHorloge>().MaintenantUtc,
            sp.GetRequiredService<ILogger<JournalDecisionsFichier>>()));
        services.AddSingleton<ILecteurEtatServeur1, LecteurEtatServeur1Signe>();
        services.AddSingleton<SondeServeur1>();
        services.AddSingleton<VigieSecondServeur>();
        return services;
    }
}

public static class VigieEndpoints
{
    public static void MapVigie(this RouteGroupBuilder api)
    {
        // POST /api/vigie/rapport : serveur 2 (veille) seulement ; jeton FACULTATIF (invalide = traité comme absent, jamais 401)
        api.MapPost("/vigie/rapport", async (RapportPosteDto? d, HttpContext http, IVeilleServeur mode, VigieSecondServeur vigie, ILogger<VigieSecondServeur> log, CancellationToken ct) =>
        {
            if (!mode.EstVeille)
                return Results.Problem(title: "Introuvable", detail: "Ce serveur n'a pas de vigie.", statusCode: StatusCodes.Status404NotFound,
                    type: "urn:pharmaofficine:erreur:introuvable", extensions: new Dictionary<string, object?> { ["code"] = "introuvable" });
            if (Invalide(d) is { } motif)
                return Results.Problem(title: "Rapport invalide", detail: motif, statusCode: StatusCodes.Status400BadRequest,
                    type: "urn:pharmaofficine:erreur:rapport_invalide", extensions: new Dictionary<string, object?> { ["code"] = "rapport_invalide" });
            var authentifie = false;
            if (http.Request.Headers.ContainsKey("Authorization"))
            {
                var r = await http.AuthenticateAsync(JwtBearerDefaults.AuthenticationScheme);
                authentifie = r.Succeeded && string.Equals(r.Principal?.FindFirst(PorteeConnexion.RevendicationPoste)?.Value, d!.Poste, StringComparison.Ordinal);
            }
            var adresse = http.Connection.RemoteIpAddress?.ToString() ?? "inconnue";
            var reponse = await vigie.TraiterRapportAsync(new RapportPoste(d!.Poste, d.Serveur1Joint, d.EchecDepuisUtc, d.VersionPoste ?? "", d.AdresseServeur1 ?? ""), adresse, authentifie, ct);
            switch (reponse.Statut)
            {
                case StatutRapport.TropRapide:
                    return Results.Problem(title: "Trop de rapports", detail: "Plus d'un rapport par seconde depuis cette adresse.", statusCode: StatusCodes.Status429TooManyRequests,
                        type: "urn:pharmaofficine:erreur:trop_de_rapports", extensions: new Dictionary<string, object?> { ["code"] = "trop_de_rapports" });
                case StatutRapport.Ignore:
                    log.LogWarning("Nom de poste {Poste} annoncé par deux adresses différentes : rapport ignoré", d!.Poste);
                    break;
            }
            var c = reponse.Consigne!;
            return Results.Ok(new ConsigneVigieDto(c.Role, c.Consigne, c.Message));
        }).AllowAnonymous();
    }

    private static string? Invalide(RapportPosteDto? d)
    {
        if (d is null) return "Corps absent.";
        if (string.IsNullOrWhiteSpace(d.Poste) || d.Poste.Length > 100) return "Le nom du poste est obligatoire (1 à 100 caractères).";
        if ((d.VersionPoste?.Length ?? 0) > 50 || (d.AdresseServeur1?.Length ?? 0) > 300) return "Version ou adresse trop longue.";
        return null;
    }
}
