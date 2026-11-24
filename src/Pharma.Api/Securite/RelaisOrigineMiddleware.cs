using System.Collections.Concurrent;
using Pharma.Application.SecondServeur;

namespace Pharma.Api.Securite;

/// <summary>
/// Serveur 1 (principal) : lit les en-têtes <c>X-Pharma-Relais-*</c> d'une requête relayée par le 2e serveur. L'adresse d'origine annoncée n'est reprise que si
/// l'appel vient de l'adresse déclarée du 2e serveur ET si la signature est valide, non rejouée et dans la fenêtre d'horloge ; sinon les en-têtes sont IGNORÉS
/// (la requête est traitée avec l'adresse réelle ; aucune erreur renvoyée, rien ne révèle le mécanisme ; un refus par 10 s au plus au journal, sans valeur reçue).
/// Une requête relayée authentique est marquée (<see cref="ViaRelais"/>) : elle ne reçoit JAMAIS un privilège lié à l'adresse (PC serveur, boucle locale),
/// même si le 2e serveur tourne sur le même PC ou si l'origine annoncée est une adresse locale (alors non reprise). Inactif sans 2e serveur déclaré, sans clé,
/// et sur un serveur en veille. À placer avant la redirection HTTPS et la limite de débit : elles voient ainsi l'adresse d'origine.
/// </summary>
public sealed class RelaisOrigineMiddleware(RequestDelegate suite, ILogger<RelaisOrigineMiddleware> journal)
{
    public const string ItemViaRelais = "Pharma.ViaRelais";
    private const int PostesJournalMax = 200;
    private long dernierRefus;
    private readonly ConcurrentDictionary<string, long> journalPoste = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Vrai si la requête est une requête relayée authentique (adresse d'appel ET signature vérifiées).</summary>
    public static bool ViaRelais(HttpContext ctx) => ctx.Items.TryGetValue(ItemViaRelais, out var v) && v is true;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Request.Headers;
        if (!h.ContainsKey(EnTetesRelais.Signature) || ctx.RequestServices.GetService<SignatureInterServeurs>() is not { } signature
            || ctx.RequestServices.GetService<ControleAppelantInterServeurs>() is not { Declare: true } controle
            || ctx.RequestServices.GetService<IVeilleServeur>() is not { EstVeille: false })
        {
            await suite(ctx);
            return;
        }
        // Adresse d'appel d'abord : un tiers du réseau ne consomme ni calcul de signature ni nonce
        if (!await controle.AutoriseAsync(ctx.Connection.RemoteIpAddress, ctx.RequestAborted))
        {
            Refuser("appel ne venant pas de l'adresse déclarée du 2e serveur");
            await suite(ctx);
            return;
        }
        var recus = new EnTetesRelaisRecus(Un(h, EnTetesRelais.Serveur), Un(h, EnTetesRelais.Poste), Un(h, EnTetesRelais.Origine),
            Un(h, EnTetesRelais.Horodatage), Un(h, EnTetesRelais.Nonce), Un(h, EnTetesRelais.Signature));
        if (signature.VerifierRelais(ctx.Request.Method, InterServeursMiddleware.CibleBrute(ctx), recus) is { } refus)
        {
            // Cache de nonces du relais plein : vérification impossible. La requête n'est PAS traitée avec l'adresse du 2e serveur : refusée (503), le poste réessaiera
            if (refus == RefusSignature.CapaciteAtteinte)
            {
                Refuser("cache de nonces du relais plein : requête relayée refusée");
                await Results.Problem(title: "Relais indisponible", detail: "Le relais est momentanément saturé : réessayez.", statusCode: StatusCodes.Status503ServiceUnavailable,
                    type: "urn:pharmaofficine:erreur:relais_indisponible", extensions: new Dictionary<string, object?> { ["code"] = "relais_indisponible" }).ExecuteAsync(ctx);
                return;
            }
            Refuser(refus switch
            {
                RefusSignature.EnTetesInvalides => "en-têtes de relais absents ou invalides",
                RefusSignature.HorodatageHorsFenetre => "horodatage de relais hors fenêtre",
                RefusSignature.NonceRejoue => "nonce de relais déjà utilisé (rejeu)",
                RefusSignature.CapaciteAtteinte => "cache de nonces du relais plein",
                _ => "signature de relais fausse"
            });
            await suite(ctx);
            return;
        }

        // Requête relayée authentique : marquée avant tout, puis l'adresse d'origine est reprise si elle n'ouvre aucun privilège local
        ctx.Items[ItemViaRelais] = true;
        if (OrigineRelais.Lire(recus.Origine) is { } origine) ctx.Connection.RemoteIpAddress = origine;
        if (NomPoste.DepuisEnTete(recus.Poste) is { } poste) Noter(poste);
        await suite(ctx);
    }

    // Plusieurs valeurs pour un même en-tête : douteux, traité comme absent
    private static string? Un(IHeaderDictionary h, string nom) => h.TryGetValue(nom, out var v) && v.Count == 1 ? v[0] : null;

    /// <summary>Un refus par 10 s au plus (un inconnu du réseau ne peut pas noyer le journal) ; ni valeur reçue, ni nom, ni secret.</summary>
    private void Refuser(string cause)
    {
        var t = Environment.TickCount64;
        var vu = Interlocked.Read(ref dernierRefus);
        if (t - vu < 10_000 || Interlocked.CompareExchange(ref dernierRefus, t, vu) != vu) return;
        journal.LogWarning("En-têtes de relais ignorés : {Cause}", cause);
    }

    /// <summary>Journal « via relais » : le poste (nom signé, donc authentique), au plus une ligne par minute et par poste ; aucune donnée de patient, ni jeton.</summary>
    private void Noter(string poste)
    {
        var t = Environment.TickCount64;
        if (journalPoste.TryGetValue(poste, out var dernier) && t - dernier < 60_000) return;
        if (journalPoste.Count >= PostesJournalMax) journalPoste.Clear();
        journalPoste[poste] = t;
        journal.LogInformation("Requêtes reçues via le relais du 2e serveur pour le poste {Poste}", poste);
    }
}
